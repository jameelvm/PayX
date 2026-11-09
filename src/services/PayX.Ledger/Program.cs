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
// deploy step. Migrations run as the tables' owner (LedgerMigrations); the
// service itself uses payx_ledger_app (Ledger), which can only SELECT and
// INSERT (7.2). Two credentials, so the one in every request's path can't
// alter history even if the code tried.
await using (var owner = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>()
    .UseNpgsql(builder.Configuration.GetConnectionString("LedgerMigrations")
        ?? throw new InvalidOperationException("Missing configuration: ConnectionStrings:LedgerMigrations"))
    .UseSnakeCaseNamingConvention().Options))
{
    await owner.Database.MigrateAsync();
}

app.MapPayXDefaults();

app.Run();
