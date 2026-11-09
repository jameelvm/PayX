using Microsoft.EntityFrameworkCore;
using PayX.Contracts.Events;
using PayX.Ledger.Jobs;
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
builder.Services.AddScoped<BalanceAudit>();

// Transaction completion (7.3): every payment event is read from Kafka and
// booked; the offset is committed only after the booking committed. Events
// that can't be booked go to the dead-letter topic (6.6), so one bad event
// never stops the rest of its partition.
builder.AddDeadLetteringConsumer<PaymentEventHandler>(Topics.Payments, "payx-ledger", Topics.PaymentsDeadLetter);

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
