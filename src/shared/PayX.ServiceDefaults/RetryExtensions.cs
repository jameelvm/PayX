using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace PayX.ServiceDefaults;

/// <summary>
/// Retries for calls that carry an idempotency key (6.3; DESIGN decision 15).
/// Doc ch.2, transient failures: "retry with exponential backoff".
/// </summary>
public static class RetryExtensions
{
    /// <summary>
    /// A total timeout around retries around a per-attempt timeout, read from
    /// <paramref name="section"/> (<c>TotalTimeout</c>, <c>AttemptTimeout</c>,
    /// <c>MaxRetries</c>, <c>BaseDelay</c>).
    ///
    /// <para><b>Only for keyed calls.</b> Retrying a POST is normally unsafe:
    /// the first attempt may have worked and only its answer was lost. Here
    /// every attempt sends the same <c>Idempotency-Key</c>, so the PSP either
    /// performs the operation once or replays what it already did. Without
    /// the key, this retry would be a double charge.</para>
    ///
    /// <para><b>What is retried:</b> no answer (connection refused, attempt
    /// timeout) and answers that say "not done, try again" (5xx, 408, 429:
    /// Polly's HTTP defaults). Never a 4xx refusal or a decline: the same
    /// request would get the same "no".</para>
    ///
    /// <para><b>Exponential backoff with jitter:</b> waits of about 200 ms,
    /// 400 ms, 800 ms…, each randomised. Backoff gives a struggling PSP room
    /// to recover; jitter stops a thousand clients that failed together from
    /// retrying together (a "thundering herd" that knocks it over again).
    /// A <c>Retry-After</c> header from the PSP is honoured.</para>
    /// </summary>
    public static IHttpClientBuilder AddKeyedRetries(this IHttpClientBuilder client, IConfigurationSection section)
    {
        var total = section.GetValue("TotalTimeout", TimeSpan.FromSeconds(10));
        var attempt = section.GetValue("AttemptTimeout", TimeSpan.FromSeconds(3));
        var retries = section.GetValue("MaxRetries", 2);
        var delay = section.GetValue("BaseDelay", TimeSpan.FromMilliseconds(200));

        // HttpClient's own timeout must not cut in before the pipeline's total.
        client.ConfigureHttpClient(http => http.Timeout = total + TimeSpan.FromSeconds(5));

        client.AddResilienceHandler("keyed-retries", pipeline => pipeline
            // Outermost: the whole call, all attempts and waits included, gets
            // at most this long. The caller's own budget is planned around it.
            .AddTimeout(total)
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = retries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = delay,
                // Polly would skip POST if told to (DisableForUnsafeHttpMethods);
                // here it must not: the key is what makes POST safe to repeat.
            })
            // Innermost: one attempt. A hung connection becomes a retry
            // instead of eating the whole budget.
            .AddTimeout(attempt));
        return client;
    }
}
