using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayX.Contracts;
using PayX.Payment.Domain;
using PayX.Payment.Repositories;
using PayX.Payment.Services;

namespace PayX.Payment.Tests;

// The orchestration against the real payx_payments database, with Vault
// replaced by a stub that records what Payment sent and answers as told.
public sealed class PaymentAuthorizationTests
{
    private static readonly Guid Customer = Guid.NewGuid(), Merchant = Guid.NewGuid();
    private static readonly Money Amount = new(5_000, "USD");

    private readonly StubVault _vault = new();

    public PaymentAuthorizationTests()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    [Fact]
    public async Task Approved_payment_ends_Authorized_with_the_psp_reference()
    {
        _vault.Answer(HttpStatusCode.OK, """{"authorizationId":"auth_1","status":"authorized","authorizationCode":"A1B2C3","declineCode":null}""");

        var result = await Authorize();

        Assert.Null(result.Failure);
        var saved = await Reload(result.Payment.Id);
        Assert.Equal((PaymentStatus.Authorized, "auth_1", "A1B2C3"), (saved.Status, saved.PspAuthorizationId, saved.AuthorizationCode));
    }

    [Fact]
    public async Task Vault_is_asked_by_token_with_a_key_fixed_to_the_payment()
    {
        _vault.Answer(HttpStatusCode.OK, """{"authorizationId":"auth_1","status":"authorized","authorizationCode":"A1B2C3"}""");

        var result = await Authorize();

        Assert.Equal("http://vault.test/api/vault/cards/tok_test/authorizations", _vault.LastUri);
        Assert.Equal($"{result.Payment.Id}:authorize", _vault.LastIdempotencyKey);
        var body = JsonDocument.Parse(_vault.LastBody!).RootElement;
        Assert.Equal((5_000, "USD", Merchant.ToString()), (body.GetProperty("amount").GetInt64(),
            body.GetProperty("currency").GetString(), body.GetProperty("merchantId").GetString()));
    }

    [Fact]
    public async Task Declined_payment_ends_Declined_with_the_code()
    {
        _vault.Answer(HttpStatusCode.OK, """{"authorizationId":"auth_2","status":"declined","authorizationCode":null,"declineCode":"insufficient_funds"}""");

        var result = await Authorize();

        Assert.Null(result.Failure);
        var saved = await Reload(result.Payment.Id);
        Assert.Equal((PaymentStatus.Declined, "insufficient_funds"), (saved.Status, saved.DeclineCode));
    }

    [Theory]
    [InlineData(RiskDecision.Block, PaymentStatus.RiskBlocked)]
    [InlineData(RiskDecision.Challenge, PaymentStatus.Challenged)]
    public async Task Blocked_or_challenged_by_risk_never_reaches_vault(RiskDecision decision, PaymentStatus expected)
    {
        var result = await Authorize(risk: new FixedRisk(decision, 65, "amount_high", "cross_border"));

        Assert.Null(result.Failure);
        var saved = await Reload(result.Payment.Id);
        Assert.Equal((expected, 65, "amount_high,cross_border"), (saved.Status, saved.RiskScore, saved.RiskReasons));
        Assert.Equal(0, _vault.Calls);
    }

    [Fact]
    public async Task Allowed_payment_keeps_its_risk_assessment()
    {
        _vault.Answer(HttpStatusCode.OK, """{"authorizationId":"auth_1","status":"authorized","authorizationCode":"A1B2C3"}""");

        var result = await Authorize(risk: new FixedRisk(RiskDecision.Allow, 15, "card_country_unknown"));

        var saved = await Reload(result.Payment.Id);
        Assert.Equal((PaymentStatus.Authorized, 15, "card_country_unknown"), (saved.Status, saved.RiskScore, saved.RiskReasons));
    }

    [Fact]
    public async Task A_fallback_approval_is_recorded_without_a_score()
    {
        _vault.Answer(HttpStatusCode.OK, """{"authorizationId":"auth_1","status":"authorized","authorizationCode":"A1B2C3"}""");

        var result = await Authorize(risk: new FixedRisk(RiskDecision.Allow, null, FallbackRiskCheck.FallbackReason));

        var saved = await Reload(result.Payment.Id);
        Assert.Equal((PaymentStatus.Authorized, (int?)null, "risk_unavailable_fallback"),
            (saved.Status, saved.RiskScore, saved.RiskReasons));
    }

    [Fact]
    public async Task Without_an_answer_from_risk_the_payment_stays_Created()
    {
        var result = await Authorize(risk: new FailingRisk(502, "risk_unreachable"));

        Assert.Equal((502, "risk_unreachable"), (result.FailureStatus, result.Failure));
        var saved = await Reload(result.Payment.Id);
        Assert.Equal((PaymentStatus.Created, null), (saved.Status, saved.RiskScore));
        Assert.Equal(0, _vault.Calls);
    }

    [Fact]
    public async Task A_timeout_makes_the_payment_Unknown_not_failed()
    {
        _vault.Answer(HttpStatusCode.GatewayTimeout, """{"title":"psp_timeout","status":504}""");

        var result = await Authorize();

        Assert.Null(result.Failure);   // no outcome yet is not an error: answered 202
        Assert.Equal(PaymentStatus.Unknown, (await Reload(result.Payment.Id)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"authorizationId":"auth_9","status":"authorized","authorizationCode":"Z9Y8X7"}""", PaymentStatus.Authorized)]
    [InlineData(HttpStatusCode.OK, """{"authorizationId":"auth_9","status":"declined","declineCode":"card_declined"}""", PaymentStatus.Declined)]
    [InlineData(HttpStatusCode.BadGateway, """{"title":"psp_unreachable","status":502}""", PaymentStatus.Unknown)]
    [InlineData(HttpStatusCode.Conflict, """{"title":"cvc_unavailable","status":409}""", PaymentStatus.Unknown)]
    public async Task An_Unknown_payment_asked_again_takes_the_banks_answer_or_stays_Unknown(
        HttpStatusCode status, string body, PaymentStatus expected)
    {
        _vault.Answer(HttpStatusCode.GatewayTimeout, """{"title":"psp_timeout","status":504}""");
        var key = new CurrentIdempotencyKey { Record = new IdempotencyRecord("caller", $"k-{Guid.NewGuid():N}", "hash", DateTimeOffset.UtcNow.AddSeconds(30)) };
        var first = await Authorize(key: key);
        Assert.Equal(PaymentStatus.Unknown, (await Reload(first.Payment.Id)).Status);

        _vault.Answer(status, body);
        var again = await Authorize(key: key);   // the retry resumes the same payment

        Assert.Equal(first.Payment.Id, again.Payment.Id);
        Assert.Null(again.Failure);
        Assert.Equal(expected, (await Reload(first.Payment.Id)).Status);
        Assert.Equal($"{first.Payment.Id}:authorize", _vault.LastIdempotencyKey);   // same key to the bank
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "cvc_unavailable")]
    [InlineData(HttpStatusCode.BadGateway, "psp_unreachable")]
    public async Task Without_an_answer_the_payment_stays_RiskApproved(HttpStatusCode status, string error)
    {
        _vault.Answer(status, $$"""{"title":"{{error}}","status":{{(int)status}}}""");

        var result = await Authorize();

        Assert.Equal(((int)status, error), (result.FailureStatus, result.Failure));
        Assert.Equal(PaymentStatus.RiskApproved, (await Reload(result.Payment.Id)).Status);
    }

    private Task<AuthorizationResult> Authorize(IRiskCheck? risk = null, CurrentIdempotencyKey? key = null)
    {
        var client = new VaultClient(new HttpClient(_vault) { BaseAddress = new Uri("http://vault.test/") });
        return new PaymentAuthorization(NewContext(), risk ?? FixedRisk.Allow(), client, key ?? new CurrentIdempotencyKey())
            .AuthorizeAsync(Customer, Merchant, Amount, "tok_test", default);
    }

    private static async Task<Domain.Payment> Reload(string id)
    {
        await using var db = NewContext();
        return await db.Payments.SingleAsync(p => p.Id == id);
    }

    private static PaymentsDbContext NewContext() => new(new DbContextOptionsBuilder<PaymentsDbContext>()
        .UseNpgsql("Host=localhost;Port=5434;Database=payx_payments;Username=payx_payments;Password=payx_payments_dev")
        .UseSnakeCaseNamingConvention()
        .Options);

    private sealed class StubVault : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";

        public int Calls { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastIdempotencyKey { get; private set; }

        public void Answer(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri!.ToString();
            LastBody = await request.Content!.ReadAsStringAsync(ct);
            LastIdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
