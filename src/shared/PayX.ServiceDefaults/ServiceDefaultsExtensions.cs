using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PayX.ServiceDefaults.Health;

namespace PayX.ServiceDefaults;

/// <summary>
/// The wiring every PayX service host shares. Each service's Program.cs calls
/// these two methods and then adds only what is specific to it — so a
/// cross-cutting concern (health checks in 1.5, OpenTelemetry later) is added
/// here once and every service picks it up.
/// </summary>
public static class ServiceDefaultsExtensions
{
    public static WebApplicationBuilder AddPayXDefaults(this WebApplicationBuilder builder)
    {
        // Controllers, not minimal APIs — same choice as JameX/SuggestX, for
        // consistent model-state validation.
        builder.Services.AddControllers();

        // Every error leaves a service as an RFC 7807 ProblemDetails body, so
        // callers (and later the Gateway) see one error shape everywhere.
        builder.Services.AddProblemDetails();

        // The registry each service adds its own readiness checks to.
        builder.Services.AddHealthChecks();

        return builder;
    }

    /// <summary>
    /// Registers a readiness check against the service's own database, read
    /// from <c>ConnectionStrings:{name}</c>.
    /// </summary>
    public static WebApplicationBuilder AddPostgresReadiness(this WebApplicationBuilder builder, string name)
    {
        // Fail at startup, not on the first request: a service with no
        // connection string is misconfigured, and the sooner it says so the
        // shorter the bad deploy.
        var connectionString = builder.Configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException($"Missing configuration: ConnectionStrings:{name}");

        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            name: $"postgres:{name}",
            factory: _ => new PostgresHealthCheck(connectionString),
            failureStatus: HealthStatus.Unhealthy,
            tags: [HealthEndpoints.ReadyTag],
            // A probe that hangs is worse than one that fails: the load
            // balancer is waiting on it. Give up after 2 seconds.
            timeout: TimeSpan.FromSeconds(2)));

        return builder;
    }

    public static WebApplication MapPayXDefaults(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.MapControllers();
        app.MapPayXHealthEndpoints();
        return app;
    }
}
