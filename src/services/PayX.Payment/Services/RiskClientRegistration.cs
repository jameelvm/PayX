using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace PayX.Payment.Services;

public static class RiskClientRegistration
{
    /// <summary>
    /// The Risk client, its resilience pipeline and its fallback, in one
    /// place so the tests exercise exactly what the service runs.
    /// Payment asks <see cref="IRiskCheck"/> = <see cref="FallbackRiskCheck"/>,
    /// which asks <see cref="RiskClient"/>, whose HttpClient runs the pipeline.
    /// </summary>
    public static IHttpClientBuilder AddRiskClient(this IServiceCollection services, IConfiguration config)
    {
        var baseUrl = config["Risk:BaseUrl"]
            ?? throw new InvalidOperationException("Missing configuration: Risk:BaseUrl");
        var timeout = config.GetValue("Risk:Timeout", TimeSpan.FromSeconds(2));
        var breaker = config.GetSection("Risk:CircuitBreaker");

        var client = services.AddHttpClient<RiskClient>(http => http.BaseAddress = new Uri(baseUrl));
        client.AddResilienceHandler("risk", pipeline => pipeline
            // Doc ch.2, "fallbacks" + "timeouts". Order matters: the first
            // strategy added is the outermost. The breaker wraps the timeout,
            // so every timeout counts as a failure it can trip on.
            //
            // Circuit breaker (5.7): when Risk keeps failing, stop asking for
            // a while. Each call to a dead Risk costs a checkout 2 s (the
            // timeout) and Risk more load while it tries to recover; an open
            // circuit fails in microseconds and leaves Risk alone. After the
            // break, one trial call ("half-open") decides: success closes the
            // circuit, failure opens it again.
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = breaker.GetValue("FailureRatio", 0.5),
                MinimumThroughput = breaker.GetValue("MinimumThroughput", 5),
                SamplingDuration = breaker.GetValue("SamplingDuration", TimeSpan.FromSeconds(30)),
                BreakDuration = breaker.GetValue("BreakDuration", TimeSpan.FromSeconds(15)),
            })
            // Timeout (5.6): Risk sits on every checkout's critical path:
            // without a bound, a slow Risk makes every checkout slow, and
            // every waiting request holds a thread, a DB connection and an
            // idempotency lock until Payment itself falls over.
            .AddTimeout(timeout));

        // What Payment actually calls: Risk, or the fallback when Risk can't answer (5.7).
        services.AddScoped<IRiskCheck, FallbackRiskCheck>();
        return client;
    }
}
