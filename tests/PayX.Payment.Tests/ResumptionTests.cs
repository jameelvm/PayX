using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayX.Contracts;
using PayX.Payment.Domain;
using PayX.Payment.Repositories;
using PayX.Payment.Services;

namespace PayX.Payment.Tests;

// 4.6: a retry that takes over a key continues the payment the key already
// created, from the state it reached, instead of creating a second payment.
public sealed class ResumptionTests
{
    private static readonly Guid Customer = Guid.NewGuid(), Merchant = Guid.NewGuid();
    private static readonly Money Amount = new(5_000, "USD");
    private readonly string _key = $"checkout-{Guid.NewGuid():N}";

    public ResumptionTests()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    [Fact]
    public async Task A_new_payment_is_attached_to_its_key_in_the_same_save()
    {
        // First attempt: Vault is down, so the request ends without an outcome.
        var (_, firstPayment) = await Attempt(HttpStatusCode.BadGateway, """{"title":"psp_unreachable"}""");

        await using var db = NewContext();
        var record = await db.IdempotencyRecords.SingleAsync(r => r.Scope == Customer.ToString() && r.Key == _key);
        Assert.Equal(firstPayment.Id, record.PaymentId);
    }

    [Fact]
    public async Task A_retry_resumes_the_same_payment_at_the_vault_step_with_the_same_psp_key()
    {
        var (_, first) = await Attempt(HttpStatusCode.BadGateway, """{"title":"psp_unreachable"}""");
        Assert.Equal(PaymentStatus.RiskApproved, first.Status);

        var (vault, second) = await Attempt(HttpStatusCode.OK,
            """{"authorizationId":"auth_9","status":"authorized","authorizationCode":"A1B2C3"}""");

        Assert.Equal(first.Id, second.Id);   // same payment, not a new one
        Assert.Equal(PaymentStatus.Authorized, second.Status);
        Assert.Equal($"{first.Id}:authorize", vault.LastIdempotencyKey);
        await using var db = NewContext();
        Assert.Equal(1, await db.Payments.CountAsync(p => p.CustomerId == Customer && p.CardToken == _key));
    }

    [Fact]
    public async Task Resuming_a_payment_that_already_reached_an_outcome_does_not_call_vault_again()
    {
        var (_, first) = await Attempt(HttpStatusCode.OK,
            """{"authorizationId":"auth_9","status":"authorized","authorizationCode":"A1B2C3"}""", complete: false);

        var (vault, second) = await Attempt(HttpStatusCode.OK, "{}");

        Assert.Equal((first.Id, PaymentStatus.Authorized), (second.Id, second.Status));
        Assert.Equal(0, vault.Calls);
    }

    // One request through the idempotency claim and the orchestration, the
    // way the filter runs it. The card token is the key, so each test's
    // payments are easy to count. The key is released afterwards unless it
    // completes, as the filter does after a 5xx (complete: false simulates a
    // process that died after saving Authorized but before storing its answer).
    private async Task<(StubVault, Domain.Payment)> Attempt(HttpStatusCode vaultStatus, string vaultBody, bool complete = false)
    {
        await using var db = NewContext();
        var idempotency = new IdempotencyService(db, new ConfigurationBuilder().Build());
        var claim = await idempotency.ClaimAsync(Customer.ToString(), _key, "hash", default);
        Assert.Contains(claim.Outcome, new[] { ClaimOutcome.Claimed, ClaimOutcome.Resumed });

        var vault = new StubVault(vaultStatus, vaultBody);
        var current = new CurrentIdempotencyKey { Record = claim.Record, Resumed = claim.Outcome == ClaimOutcome.Resumed };
        var result = await new PaymentAuthorization(db, new AllowAllRiskCheck(),
                new VaultClient(new HttpClient(vault) { BaseAddress = new Uri("http://vault.test/") }), current)
            .AuthorizeAsync(Customer, Merchant, Amount, _key, default);

        if (complete) await idempotency.CompleteAsync(claim.Record!, 201, "{}", null, default);
        else await idempotency.ReleaseAsync(claim.Record!, default);
        return (vault, result.Payment);
    }

    private static PaymentsDbContext NewContext() => new(new DbContextOptionsBuilder<PaymentsDbContext>()
        .UseNpgsql("Host=localhost;Port=5434;Database=payx_payments;Username=payx_payments;Password=payx_payments_dev")
        .UseSnakeCaseNamingConvention()
        .Options);

    private sealed class StubVault(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastIdempotencyKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastIdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
