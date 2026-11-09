using Microsoft.EntityFrameworkCore;
using PayX.Ledger.Repositories;

namespace PayX.Ledger.Tests;

// The real payx_ledger database (docker compose up -d postgres), as in
// Payment's tests. Every test uses fresh owner and payment ids, so tests
// never see each other's rows and the database never needs wiping (it
// can't be wiped anyway: the ledger is append-only, 7.2).
internal static class LedgerDb
{
    // What the service runs as (7.2): SELECT/INSERT only.
    public const string Connection = "Host=localhost;Port=5434;Database=payx_ledger;Username=payx_ledger_app;Password=payx_ledger_app_dev";
    // The tables' owner: migrations, and tests showing even the owner is stopped.
    public const string OwnerConnection = "Host=localhost;Port=5434;Database=payx_ledger;Username=payx_ledger;Password=payx_ledger_dev";

    private static readonly Lazy<bool> Migrated = new(() =>
    {
        using var db = Create(OwnerConnection);
        db.Database.Migrate();
        return true;
    });

    public static LedgerDbContext NewContext()
    {
        _ = Migrated.Value;
        return Create(Connection);
    }

    private static LedgerDbContext Create(string connection) =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connection).UseSnakeCaseNamingConvention().Options);
}
