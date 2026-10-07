using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

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

        return builder;
    }

    public static WebApplication MapPayXDefaults(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.MapControllers();
        return app;
    }
}
