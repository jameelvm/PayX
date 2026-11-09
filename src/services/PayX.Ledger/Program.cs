using Microsoft.EntityFrameworkCore;
using PayX.Ledger.Repositories;
using PayX.Ledger.Services;
using PayX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();
// Ledger is ready only if it can reach its own database, payx_ledger.
builder.AddPostgresReadiness("Ledger");

builder.Services.AddDbContext<LedgerDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Ledger"))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AccountRepository>();
builder.Services.AddScoped<LedgerWriter>();

var app = builder.Build();

// Local development only, as in Payment: production migrates in a separate
// deploy step.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
}

app.MapPayXDefaults();

app.Run();
