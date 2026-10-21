using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PayX.Payment.Repositories;
using PayX.Payment.Services;
using PayX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();
// Payment is ready only if it can reach its own database, payx_payments.
builder.AddPostgresReadiness("Payments");

// Payment checks the caller's token itself (same rules as the Gateway, 2.4)
// and denies by default; health endpoints opt out explicitly.
builder.AddPayXJwtAuthentication();
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddDbContext<PaymentsDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Payments"))
    .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<PaymentAuthorization>();
builder.Services.AddSingleton<IRiskCheck, AllowAllRiskCheck>();   // stand-in until Phase 5

// Typed client for Vault. A plain timeout for now; retries and circuit
// breaking (Polly) come in Phases 5-6.
builder.Services.AddHttpClient<VaultClient>(http =>
{
    http.BaseAddress = new Uri(builder.Configuration["Vault:BaseUrl"]
        ?? throw new InvalidOperationException("Missing configuration: Vault:BaseUrl"));
    http.Timeout = builder.Configuration.GetValue("Vault:Timeout", TimeSpan.FromSeconds(15));
});

var app = builder.Build();

// Local development only, as in Identity (Module 2.1): production migrates
// in a separate deploy step.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
}

// Same order as the Gateway: error formatting first, so 401/403 have bodies.
app.MapPayXDefaults();
app.UseAuthentication();
app.UseAuthorization();

app.Run();
