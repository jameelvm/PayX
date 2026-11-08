using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayX.Contracts;
using PayX.Payment.Domain;
using PayX.Payment.Repositories;
using PayX.Payment.Services;

namespace PayX.Payment.Tests;

// The resolver (6.5) against the real payx_payments database, with the PSP
// replaced by a stub that answers the lookup by reference as told.
public sealed class StuckPaymentResolverTests
{
    private readonly StubPsp _psp = new();

    public StuckPaymentResolverTests()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    [Fact]
    public async Task An_Unknown_payment_the_bank_authorized_becomes_Authorized()
    {
        var id = await Stuck(PaymentStatus.Unknown);
        _psp.Answer(HttpStatusCode.OK, """[{"authorizationId":"auth_77","status":"authorized","authorizationCode":"K7K7K7"}]""");

        Assert.Equal(PaymentStatus.Authorized, await Resolver().ResolveAsync(id, default));

        var saved = await Reload(id);
        Assert.Equal((PaymentStatus.Authorized, "auth_77", "K7K7K7"), (saved.Status, saved.PspAuthorizationId, saved.AuthorizationCode));
        Assert.Equal($"http://psp.test/api/psp/authorizations?reference={id}", _psp.LastUri);   // asked by our payment id
        Assert.Contains("payment.authorized", await Events(id));                              // announced like any move
    }

    [Fact]
    public async Task A_payment_stuck_at_RiskApproved_the_bank_declined_becomes_Declined()
    {
        var id = await Stuck(PaymentStatus.RiskApproved);
        _psp.Answer(HttpStatusCode.OK, """[{"authorizationId":"auth_78","status":"declined","declineCode":"insufficient_funds"}]""");

        Assert.Equal(PaymentStatus.Declined, await Resolver().ResolveAsync(id, default));
        Assert.Equal("insufficient_funds", (await Reload(id)).DeclineCode);
    }

    [Theory]
    [InlineData(PaymentStatus.Unknown)]
    [InlineData(PaymentStatus.RiskApproved)]
    public async Task No_record_at_the_bank_means_Failed(PaymentStatus stuckAt)
    {
        var id = await Stuck(stuckAt);
        _psp.Answer(HttpStatusCode.OK, "[]");

        Assert.Equal(PaymentStatus.Failed, await Resolver().ResolveAsync(id, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_PSP_that_cant_answer_proves_nothing(HttpStatusCode status)
    {
        var id = await Stuck(PaymentStatus.Unknown);
        _psp.Answer(status, "{}");

        Assert.Null(await Resolver().ResolveAsync(id, default));
        Assert.Equal(PaymentStatus.Unknown, (await Reload(id)).Status);   // not Failed: try again later
    }

    [Fact]
    public async Task A_payment_already_resolved_is_left_alone()
    {
        var id = await Stuck(PaymentStatus.Unknown);
        await using (var db = NewContext())
        {
            var p = await db.Payments.SingleAsync(x => x.Id == id);
            p.Authorize("auth_1", "A1B2C3");   // the customer's retry got there first
            await db.SaveChangesAsync();
        }

        Assert.Equal(PaymentStatus.Authorized, await Resolver().ResolveAsync(id, default));
        Assert.Equal(0, _psp.Calls);
    }

    [Fact]
    public async Task Only_payments_older_than_the_grace_period_are_picked_up()
    {
        var old = await Stuck(PaymentStatus.Unknown);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        var young = await Stuck(PaymentStatus.Unknown);
        var settled = await Stuck(PaymentStatus.Unknown);
        await using (var db = NewContext())
        {
            var p = await db.Payments.SingleAsync(x => x.Id == settled);
            p.Decline("card_declined");
            await db.SaveChangesAsync();
        }

        var picked = await Resolver().FindStuckAsync(TimeSpan.FromSeconds(1), 100_000, default);

        Assert.Contains(old, picked);
        Assert.DoesNotContain(young, picked);     // may still get its answer the normal way
        Assert.DoesNotContain(settled, picked);   // not stuck
    }

    private StuckPaymentResolver Resolver() => new(NewContext(),
        new PspClient(new HttpClient(_psp) { BaseAddress = new Uri("http://psp.test/") }),
        TimeProvider.System, NullLogger<StuckPaymentResolver>.Instance);

    private static async Task<string> Stuck(PaymentStatus at)
    {
        var payment = new Domain.Payment(Guid.NewGuid(), Guid.NewGuid(), new Money(5_000, "USD"), "tok_test");
        await using var db = NewContext();
        db.Payments.Add(payment);
        payment.ApproveRisk();
        if (at == PaymentStatus.Unknown)
            payment.MarkUnknown();
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private static async Task<Domain.Payment> Reload(string id)
    {
        await using var db = NewContext();
        return await db.Payments.SingleAsync(p => p.Id == id);
    }

    private static async Task<List<string>> Events(string id)
    {
        await using var db = NewContext();
        return await db.OutboxMessages.Where(o => o.Key == id).OrderBy(o => o.Id).Select(o => o.EventType).ToListAsync();
    }

    private static PaymentsDbContext NewContext() => new(new DbContextOptionsBuilder<PaymentsDbContext>()
        .UseNpgsql("Host=localhost;Port=5434;Database=payx_payments;Username=payx_payments;Password=payx_payments_dev")
        .UseSnakeCaseNamingConvention()
        .Options);

    private sealed class StubPsp : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "[]";

        public int Calls { get; private set; }
        public string? LastUri { get; private set; }

        public void Answer(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
