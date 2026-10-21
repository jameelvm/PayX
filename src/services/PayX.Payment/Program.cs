using Microsoft.EntityFrameworkCore;
using PayX.Payment.Repositories;
using PayX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();
// Payment is ready only if it can reach its own database, payx_payments.
builder.AddPostgresReadiness("Payments");

builder.Services.AddDbContext<PaymentsDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Payments"))
    .UseSnakeCaseNamingConvention());

var app = builder.Build();

// Local development only, as in Identity (Module 2.1): production migrates
// in a separate deploy step.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
}

app.MapPayXDefaults();

app.Run();
