using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace PayX.ServiceDefaults.Health;

/// <summary>Readiness check: does Redis answer a PING? Same role as <see cref="PostgresHealthCheck"/>.</summary>
public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"ping {latency.TotalMilliseconds:F1} ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
