using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PayX.ServiceDefaults.Health;

/// <summary>
/// Two probes that answer two different questions:
///
///   /health/live   "Is this process alive?"        — runs NO dependency checks.
///                  Failing it means: restart me.
///   /health/ready  "Can I serve requests right now?" — runs every check tagged "ready".
///                  Failing it means: stop sending me traffic, but don't restart me.
///
/// The classic mistake is checking the database in the liveness probe. When
/// Postgres has a blip, every instance of every service fails liveness at
/// once, the orchestrator restarts them all, and they all hammer the database
/// with reconnects the moment it comes back. A restart can't fix someone
/// else's outage — it only adds a restart storm on top of it.
/// </summary>
public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static WebApplication MapPayXHealthEndpoints(this WebApplication app)
    {
        // Predicate = _ => false: run no checks at all. If the process can
        // answer this HTTP request, it is alive.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteJson,
        });

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteJson,
        });

        return app;
    }

    // The default writer returns just "Healthy" / "Unhealthy". This one also
    // says *which* dependency failed and why — the first thing you want to
    // know when a service drops out of the load balancer.
    private static Task WriteJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            durationMs = (int)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), detail = e.Value.Description }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}
