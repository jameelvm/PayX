using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayX.Contracts;
using PayX.Payment.Domain;
using PayX.Payment.Repositories;
using PayX.Payment.Services;

namespace PayX.Payment.Tests;

// Capture against the real payx_payments database, with the PSP replaced by a
// stub that records what Payment sent and answers as told.
public sealed class PaymentCaptureTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private readonly StubPsp _psp = new();

    public PaymentCaptureTests()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    [Fact]
    public async Task Full_capture_by_default_quotes_the_psp_authorization_and_a_key_fixed_to_the_payment()
    {
        var payment = await SavedPayment(authorized: true);
        _psp.Answer(HttpStatusCode.OK, """{"status":"captured"}""");

        var result = await Capture(payment.Id, Merchant, amountMinor: null);

        Assert.Null(result.Failure);
        Assert.Equal("http://psp.test/api/psp/authorizations/auth_test/capture", _psp.LastUri);
        Assert.Equal($"{payment.Id}:capture", _psp.LastIdempotencyKey);
        Assert.Equal(5_000, JsonDocument.Parse(_psp.LastBody!).RootElement.GetProperty("amount").GetInt64());
        var saved = await Reload(payment.Id);
        Assert.Equal((PaymentStatus.Captured, (long?)5_000), (saved.Status, saved.CapturedAmountMinor));
    }

    [Fact]
    public async Task Partial_capture_records_the_partial_amount()
    {
        var payment = await SavedPayment(authorized: true);
        _psp.Answer(HttpStatusCode.OK, """{"status":"captured"}""");

        await Capture(payment.Id, Merchant, amountMinor: 3_000);

        Assert.Equal(3_000, (await Reload(payment.Id)).CapturedAmountMinor);
    }

    [Fact]
    public async Task Another_merchant_sees_no_such_payment()
    {
        var payment = await SavedPayment(authorized: true);

        var result = await Capture(payment.Id, Guid.NewGuid(), amountMinor: null);

        Assert.Equal((null, 404), (result.Payment, result.FailureStatus));
        Assert.Equal(0, _psp.Calls);
    }

    [Fact]
    public async Task A_declined_payment_is_refused_without_asking_the_psp()
    {
        var payment = await SavedPayment(authorized: false);

        var result = await Capture(payment.Id, Merchant, amountMinor: null);

        Assert.Equal((409, "payment_not_capturable"), (result.FailureStatus, result.Failure));
        Assert.Equal(0, _psp.Calls);
    }

    [Fact]
    public async Task More_than_authorized_is_refused_without_asking_the_psp()
    {
        var payment = await SavedPayment(authorized: true);

        var result = await Capture(payment.Id, Merchant, amountMinor: 5_001);

        Assert.Equal((422, "amount_exceeds_authorized"), (result.FailureStatus, result.Failure));
        Assert.Equal(0, _psp.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.GatewayTimeout, "psp_timeout", 504)]   // stub answers 504: same handling as a real timeout
    [InlineData(HttpStatusCode.Conflict, "authorization_not_capturable", 409)]
    public async Task Without_a_capture_at_the_psp_the_payment_stays_Authorized(HttpStatusCode pspStatus, string error, int expected)
    {
        var payment = await SavedPayment(authorized: true);
        _psp.Answer(pspStatus, $$"""{"title":"{{error}}","status":{{(int)pspStatus}}}""");

        var result = await Capture(payment.Id, Merchant, amountMinor: null);

        Assert.Equal((expected, error), (result.FailureStatus, result.Failure));
        Assert.Equal(PaymentStatus.Authorized, (await Reload(payment.Id)).Status);
    }

    [Fact]
    public async Task A_psp_that_does_not_answer_in_time_leaves_the_payment_Authorized()
    {
        var payment = await SavedPayment(authorized: true);
        _psp.Delay = TimeSpan.FromSeconds(2);

        var result = await Capture(payment.Id, Merchant, amountMinor: null, timeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal((504, "psp_timeout"), (result.FailureStatus, result.Failure));
        Assert.Equal(PaymentStatus.Authorized, (await Reload(payment.Id)).Status);
    }

    private Task<CaptureResult> Capture(string id, Guid merchant, long? amountMinor, TimeSpan? timeout = null)
    {
        var client = new PspClient(new HttpClient(_psp)
        {
            BaseAddress = new Uri("http://psp.test/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        });
        return new PaymentCapture(NewContext(), client, new CurrentIdempotencyKey()).CaptureAsync(id, merchant, amountMinor, default);
    }

    private static async Task<Domain.Payment> SavedPayment(bool authorized)
    {
        var payment = new Domain.Payment(Merchant, Guid.NewGuid(), new Money(5_000, "USD"), "tok_test");
        payment.ApproveRisk();
        if (authorized) payment.Authorize("auth_test", "A1B2C3");
        else payment.Decline("card_declined");

        await using var db = NewContext();
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
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

    private sealed class StubPsp : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";

        public int Calls { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastIdempotencyKey { get; private set; }
        public TimeSpan Delay { get; set; }

        public void Answer(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri!.ToString();
            LastBody = await request.Content!.ReadAsStringAsync(ct);
            LastIdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            await Task.Delay(Delay, ct);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
