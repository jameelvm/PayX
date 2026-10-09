using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace PayX.ServiceDefaults.Health;

/// <summary>
/// Readiness check: can this service open a connection to its own database and
/// run a trivial query? Registered only on the "ready" probe — never "live" —
/// see <see cref="HealthEndpoints"/> for why.
/// </summary>
public sealed class PostgresHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy($"connected to '{connection.Database}'");
        }
        catch (Exception ex)
        {
            // The exception message, not the connection string — a health
            // endpoint must never echo credentials back to whoever calls it.
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
