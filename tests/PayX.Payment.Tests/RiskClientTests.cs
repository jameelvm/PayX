using System.Net;
using System.Text.Json;
using PayX.Contracts;
using PayX.Payment.Services;

namespace PayX.Payment.Tests;

// RiskClient against stubbed Vault and Risk: what it sends, and how it reads
// each kind of answer. No database, no network.
public sealed class RiskClientTests
{
    private static readonly Domain.Payment Payment =
        new(Guid.NewGuid(), Guid.NewGuid(), new Money(600_000, "USD"), "tok_test");

    private const string GbCard = """{"token":"tok_test","brand":"visa","last4":"0000","country":"GB"}""";
    private const string Challenge = """{"paymentId":"x","decision":"Challenge","score":65,"reasons":[{"code":"amount_high","points":50},{"code":"cross_border","points":15}]}""";

    [Fact]
    public async Task Asks_risk_with_the_country_vault_knows_and_returns_its_decision()
    {
        var risk = new Stub(HttpStatusCode.OK, Challenge);

        var result = await Check(new Stub(HttpStatusCode.OK, GbCard), risk);

        Assert.Equal(RiskDecision.Challenge, result.Assessment!.Decision);
        Assert.Equal(65, result.Assessment.Score);
        Assert.Equal(["amount_high", "cross_border"], result.Assessment.Reasons);

        var sent = JsonDocument.Parse(risk.LastBody!).RootElement;
        Assert.Equal("http://risk.test/api/risk/assessments", risk.LastUri);
        Assert.Equal((Payment.Id, Payment.CustomerId.ToString(), 600_000L, "USD", "GB"),
            (sent.GetProperty("paymentId").GetString(), sent.GetProperty("customerId").GetString(),
             sent.GetProperty("amountMinor").GetInt64(), sent.GetProperty("currency").GetString(),
             sent.GetProperty("cardCountry").GetString()));
    }

    [Fact]
    public async Task Without_an_answer_from_vault_the_country_is_sent_as_unknown()
    {
        var risk = new Stub(HttpStatusCode.OK, Challenge);

        await Check(new Stub(HttpStatusCode.NotFound, """{"title":"card_not_found"}"""), risk);

        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(risk.LastBody!).RootElement.GetProperty("cardCountry").ValueKind);
    }

    [Fact]
    public async Task A_failing_risk_service_is_a_502_not_a_decision()
    {
        var result = await Check(new Stub(HttpStatusCode.OK, GbCard), new Stub(HttpStatusCode.InternalServerError, "{}"));

        Assert.Null(result.Assessment);
        Assert.Equal((502, "risk_unavailable"), (result.FailureStatus, result.Failure));
    }

    [Fact]
    public async Task An_unreachable_risk_service_is_a_502()
    {
        var result = await Check(new Stub(HttpStatusCode.OK, GbCard), new Stub(throws: true));

        Assert.Equal((502, "risk_unreachable"), (result.FailureStatus, result.Failure));
    }

    private static Task<RiskCheckResult> Check(Stub vault, Stub risk)
    {
        var vaultClient = new VaultClient(new HttpClient(vault) { BaseAddress = new Uri("http://vault.test/") });
        var client = new RiskClient(new HttpClient(risk) { BaseAddress = new Uri("http://risk.test/") }, vaultClient);
        return client.CheckAsync(Payment, default);
    }

    private sealed class Stub(HttpStatusCode status = HttpStatusCode.OK, string body = "{}", bool throws = false)
        : HttpMessageHandler
    {
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (throws) throw new HttpRequestException("connection refused");
            LastUri = request.RequestUri!.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
