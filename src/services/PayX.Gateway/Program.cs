using Microsoft.AspNetCore.Authorization;
using PayX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();

// Every request's "Authorization: Bearer <jwt>" is checked here, at the edge,
// before YARP forwards anything. A bad token never reaches a service.
// (Validation rules live in ServiceDefaults since 4.2c, shared with Payment.)
builder.AddPayXJwtAuthentication();

builder.Services.AddAuthorization(options =>
{
    // Secure by default: any endpoint or route without its own policy
    // requires a valid token. Forgetting to protect a new route locks it
    // instead of leaving it open. Public routes must say so explicitly
    // ("anonymous" in appsettings.json).
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    // Coarse, service-level rules only. Which role may call which endpoint
    // inside a service is that service's decision (Phase 4 onwards).
    options.AddPolicy("payments", p => p.RequireRole("Customer", "Merchant"));
});

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// Order matters: middleware runs in the order it is added. MapPayXDefaults
// adds the error-formatting middleware (ProblemDetails, Module 1.1), so it
// must come first, or the 401/403 written by authentication below would leave
// with an empty body instead of the one error shape every service uses.
app.MapPayXDefaults();

app.UseAuthentication();
app.UseAuthorization();

app.MapReverseProxy();

app.Run();
