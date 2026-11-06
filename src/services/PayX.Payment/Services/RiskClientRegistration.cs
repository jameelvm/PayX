using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace PayX.Payment.Services;

public static class RiskClientRegistration
{
    /// <summary>
    /// The Risk client and its resilience pipeline, in one place so the tests
    /// exercise exactly what the service runs.
    /// </summary>
    public static IHttpClientBuilder AddRiskClient(this IServiceCollection services, IConfiguration config)
    {
        var baseUrl = config["Risk:BaseUrl"]
            ?? throw new InvalidOperationException("Missing configuration: Risk:BaseUrl");
        var timeout = config.GetValue("Risk:Timeout", TimeSpan.FromSeconds(2));

        var client = services.AddHttpClient<IRiskCheck, RiskClient>(http => http.BaseAddress = new Uri(baseUrl));
        client
            // Doc ch.2, "timeouts" for transient failures. Risk sits on every
            // checkout's critical path: without a bound, a slow Risk makes
            // every checkout slow, and every waiting request holds a thread,
            // a DB connection and an idempotency lock until Payment itself
            // falls over. Fail fast instead; 5.7 decides what to do then.
            //
            // A Polly pipeline, not just HttpClient.Timeout: 5.7 adds a
            // circuit breaker to this same pipeline, and the strategies
            // compose (the timeout's failures are what the breaker counts).
            .AddResilienceHandler("risk", pipeline => pipeline.AddTimeout(timeout));
        return client;
    }
}
