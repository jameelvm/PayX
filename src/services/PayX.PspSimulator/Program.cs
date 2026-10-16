using Microsoft.EntityFrameworkCore;
using PayX.PspSimulator.Repositories;
using PayX.PspSimulator.Services;
using PayX.ServiceDefaults;

// The outside world, simulated: one service standing in for the payment
// gateway/PSP (Stripe, Adyen), the card networks and the issuing banks.
// PayX talks to it the way it would talk to a real provider, over HTTP, and
// never reaches into it otherwise. No Gateway route: customers never call a
// PSP directly; only PayX's own services do.
var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();
builder.AddPostgresReadiness("Psp");

builder.Services.AddDbContext<PspDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Psp"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddScoped<Authorizer>();

var app = builder.Build();

// Local development only, as in Identity (Module 2.1): production migrates
// in a separate deploy step.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<PspDbContext>().Database.MigrateAsync();
}

app.MapPayXDefaults();

app.Run();
