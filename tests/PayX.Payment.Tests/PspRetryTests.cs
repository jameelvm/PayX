using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PayX.Payment.Services;
using PayX.ServiceDefaults;

namespace PayX.Payment.Tests;

// Keyed retries (6.3) on Payment's PSP client, wired with the same
// AddKeyedRetries the services use, against a scripted stub PSP. Short
// timings keep the tests quick; production values are in appsettings.json.
public sealed class PspRetryTests
{
    private const string Captured = """{"authorizationId":"auth_1","status":"captured"}""";

    [Fact]
    public async Task Transient_failures_are_retried_with_the_same_key_until_it_works()
    {
        var psp = new ScriptedPsp(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.BadGateway, "{}"),
            (HttpStatusCode.OK, Captured));

        var result = await Client(psp).CaptureAsync("auth_1", 5_000, "pay_1:capture", default);

        Assert.Equal(("captured", 200), (result.Status, result.StatusCode));
        Assert.Equal(3, psp.Calls);
        Assert.All(psp.Keys, k => Assert.Equal("pay_1:capture", k));   // every attempt: the same key
    }

    [Fact]
    public async Task A_refusal_is_not_retried()
    {
        var psp = new ScriptedPsp((HttpStatusCode.Conflict, """{"title":"authorization_not_capturable","status":409}"""));

        var result = await Client(psp).CaptureAsync("auth_1", 5_000, "pay_1:capture", default);

        Assert.Equal((409, "authorization_not_capturable"), (result.StatusCode, result.Error));
        Assert.Equal(1, psp.Calls);   // the same request would get the same "no"
    }

    [Fact]
    public async Task When_retries_run_out_the_last_failure_is_returned()
    {
        var psp = new ScriptedPsp((HttpStatusCode.ServiceUnavailable, """{"title":"psp_unavailable","status":503}"""));

        var result = await Client(psp).CaptureAsync("auth_1", 5_000, "pay_1:capture", default);

        Assert.Equal(503, result.StatusCode);
        Assert.Equal(3, psp.Calls);   // 1 attempt + 2 retries
    }

    [Fact]
    public async Task A_hung_attempt_is_cut_off_and_retried()
    {
        var psp = new ScriptedPsp((HttpStatusCode.OK, Captured)) { HangFirstCall = TimeSpan.FromSeconds(10) };

        var clock = Stopwatch.StartNew();
        var result = await Client(psp).CaptureAsync("auth_1", 5_000, "pay_1:capture", default);

        Assert.Equal("captured", result.Status);
        Assert.Equal(2, psp.Calls);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");   // not 10 s
    }

    [Fact]
    public async Task When_the_total_budget_runs_out_it_is_a_timeout_not_a_failure()
    {
        var psp = new ScriptedPsp((HttpStatusCode.OK, Captured)) { HangEveryCall = TimeSpan.FromSeconds(10) };

        var result = await Client(psp).CaptureAsync("auth_1", 5_000, "pay_1:capture", default);

        // 504: the PSP may have captured; a retry with the same key finds out.
        Assert.Equal((504, "psp_timeout"), (result.StatusCode, result.Error));
    }

    [Fact]
    public async Task Waits_between_attempts_grow_and_vary()
    {
        var psp = new ScriptedPsp(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.OK, Captured));

        await Client(psp, baseDelay: "00:00:00.100").CaptureAsync("auth_1", 5_000, "pay_1:capture", default);

        var gaps = psp.CallTimes.Zip(psp.CallTimes.Skip(1), (a, b) => b - a).ToList();
        Assert.Equal(2, gaps.Count);
        // Exponential with jitter: the second wait is longer on average; both are real waits.
        Assert.All(gaps, g => Assert.True(g > TimeSpan.FromMilliseconds(20), $"gap {g}"));
    }

    private static PspClient Client(ScriptedPsp psp, string baseDelay = "00:00:00.010")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Psp:Resilience:TotalTimeout"] = "00:00:01.500",
            ["Psp:Resilience:AttemptTimeout"] = "00:00:00.400",
            ["Psp:Resilience:MaxRetries"] = "2",
            ["Psp:Resilience:BaseDelay"] = baseDelay,
        }).Build();

        var services = new ServiceCollection();
        services.AddHttpClient<PspClient>(http => http.BaseAddress = new Uri("http://psp.test/"))
            .AddKeyedRetries(config.GetSection("Psp:Resilience"))
            .ConfigurePrimaryHttpMessageHandler(() => psp);
        return services.BuildServiceProvider().GetRequiredService<PspClient>();
    }

    // Answers each call with the next scripted response (the last one repeats).
    private sealed class ScriptedPsp(params (HttpStatusCode Status, string Body)[] script) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Keys { get; } = [];
        public List<DateTime> CallTimes { get; } = [];
        public TimeSpan? HangFirstCall { get; init; }
        public TimeSpan? HangEveryCall { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = Calls++;
            CallTimes.Add(DateTime.UtcNow);
            Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            if (HangEveryCall is { } always) await Task.Delay(always, ct);
            if (call == 0 && HangFirstCall is { } first) await Task.Delay(first, ct);

            var (status, body) = script[Math.Min(call, script.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
