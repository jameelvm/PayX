using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PayX.Contracts;
using PayX.Payment.Services;

namespace PayX.Payment.Tests;

// Payment's side of a failing Risk, wired exactly as Program.cs wires it
// (AddRiskClient: HttpClient + Polly pipeline + fallback), with Risk itself
// replaced by a stub. Short timings keep the tests quick; production values
// are in appsettings.json. A new service provider per test = a fresh breaker.
public sealed class RiskResilienceTests
{
    private const string Allow = """{"paymentId":"x","decision":"Allow","score":0,"reasons":[]}""";
    private const string Challenge = """{"paymentId":"x","decision":"Challenge","score":60,"reasons":[{"code":"card_country_high_risk","points":60}]}""";

    private static Domain.Payment Pay(long minor, string currency = "USD") =>
        new(Guid.NewGuid(), Guid.NewGuid(), new Money(minor, currency), "tok_test");

    // ---- timeout (5.6)

    [Fact]
    public async Task A_slow_risk_service_is_given_up_on_at_the_timeout()
    {
        var risk = Build(new RiskStub(HttpStatusCode.OK, Allow, delay: TimeSpan.FromSeconds(5)));

        var clock = Stopwatch.StartNew();
        var result = await risk.CheckAsync(Pay(50_000), default);   // above the fallback limit: the failure shows

        Assert.Equal((504, "risk_timeout"), (result.FailureStatus, result.Failure));
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));   // not 5 s
    }

    [Fact]
    public async Task A_fast_answer_passes_through_untouched()
    {
        var risk = Build(new RiskStub(HttpStatusCode.OK, Allow, delay: TimeSpan.FromMilliseconds(20)));

        Assert.Equal(RiskDecision.Allow, (await risk.CheckAsync(Pay(5_000), default)).Assessment!.Decision);
    }

    // ---- circuit breaker (5.7)

    [Fact]
    public async Task After_repeated_failures_risk_is_not_asked_at_all()
    {
        var stub = new RiskStub(HttpStatusCode.InternalServerError, "{}");
        var risk = Build(stub);

        // Minimum throughput 2, failure ratio 0.5: two failures open the circuit.
        Assert.Equal("risk_unavailable", (await risk.CheckAsync(Pay(50_000), default)).Failure);
        Assert.Equal("risk_unavailable", (await risk.CheckAsync(Pay(50_000), default)).Failure);

        var result = await risk.CheckAsync(Pay(50_000), default);

        Assert.Equal((503, "risk_circuit_open"), (result.FailureStatus, result.Failure));
        Assert.Equal(2, stub.Calls);   // the third never left Payment
    }

    [Fact]
    public async Task After_the_break_a_trial_call_closes_the_circuit_again()
    {
        var stub = new RiskStub(HttpStatusCode.InternalServerError, "{}");
        var risk = Build(stub);
        await risk.CheckAsync(Pay(50_000), default);
        await risk.CheckAsync(Pay(50_000), default);   // open

        stub.Answer(HttpStatusCode.OK, Allow);                // Risk recovers
        await Task.Delay(TimeSpan.FromMilliseconds(700));     // break duration 500 ms

        Assert.Equal(RiskDecision.Allow, (await risk.CheckAsync(Pay(50_000), default)).Assessment!.Decision);
        Assert.Equal(RiskDecision.Allow, (await risk.CheckAsync(Pay(50_000), default)).Assessment!.Decision);
        Assert.Equal(4, stub.Calls);
    }

    // ---- fallback (5.7)

    [Fact]
    public async Task Risk_down_a_small_payment_goes_ahead_unscored_and_marked()
    {
        var risk = Build(new RiskStub(HttpStatusCode.InternalServerError, "{}"));

        var result = await risk.CheckAsync(Pay(9_999), default);   // limit 10,000

        Assert.Equal(RiskDecision.Allow, result.Assessment!.Decision);
        Assert.Null(result.Assessment.Score);
        Assert.Equal([FallbackRiskCheck.FallbackReason], result.Assessment.Reasons);
    }

    [Theory]
    [InlineData(10_000, "USD")]   // at the limit: waits (the limit is exclusive)
    [InlineData(500_000, "USD")]
    [InlineData(100, "KWD")]      // no limit for the currency: always waits
    public async Task Risk_down_anything_else_waits_for_risk(long minor, string currency)
    {
        var risk = Build(new RiskStub(HttpStatusCode.InternalServerError, "{}"));

        var result = await risk.CheckAsync(Pay(minor, currency), default);

        Assert.Null(result.Assessment);
        Assert.Equal((502, "risk_unavailable"), (result.FailureStatus, result.Failure));
    }

    [Fact]
    public async Task Risk_up_its_decision_stands_even_for_a_small_payment()
    {
        var risk = Build(new RiskStub(HttpStatusCode.OK, Challenge));

        Assert.Equal(RiskDecision.Challenge, (await risk.CheckAsync(Pay(500), default)).Assessment!.Decision);
    }

    private static IRiskCheck Build(RiskStub stub)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Risk:BaseUrl"] = "http://risk.test/",
            ["Risk:Timeout"] = "00:00:00.200",
            ["Risk:CircuitBreaker:FailureRatio"] = "0.5",
            ["Risk:CircuitBreaker:MinimumThroughput"] = "2",
            ["Risk:CircuitBreaker:SamplingDuration"] = "00:00:10",
            ["Risk:CircuitBreaker:BreakDuration"] = "00:00:00.500",
            ["Risk:Fallback:AllowBelowMinor:USD"] = "10000",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpClient<VaultClient>(http => http.BaseAddress = new Uri("http://vault.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => new RiskStub(HttpStatusCode.NotFound, "{}"));
        services.AddRiskClient(config).ConfigurePrimaryHttpMessageHandler(() => stub);

        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IRiskCheck>();
    }

    // Stands in for the network: answers as told after a delay, and stops
    // waiting when the caller cancels (as a real socket read does).
    private sealed class RiskStub(HttpStatusCode status, string body, TimeSpan? delay = null) : HttpMessageHandler
    {
        private HttpStatusCode _status = status;
        private string _body = body;

        public int Calls { get; private set; }

        public void Answer(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (delay is { } d) await Task.Delay(d, ct);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
