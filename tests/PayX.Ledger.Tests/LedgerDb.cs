using Microsoft.EntityFrameworkCore;
using PayX.Ledger.Repositories;

namespace PayX.Ledger.Tests;

// The real payx_ledger database (docker compose up -d postgres), as in
// Payment's tests. Every test uses fresh owner and payment ids, so tests
// never see each other's rows and the database never needs wiping (it
// can't be wiped anyway: the ledger is append-only from 7.2).
internal static class LedgerDb
{
    public const string Connection = "Host=localhost;Port=5434;Database=payx_ledger;Username=payx_ledger;Password=payx_ledger_dev";

    private static readonly Lazy<bool> Migrated = new(() =>
    {
        using var db = Create();
        db.Database.Migrate();
        return true;
    });

    public static LedgerDbContext NewContext()
    {
        _ = Migrated.Value;
        return Create();
    }

    private static LedgerDbContext Create() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(Connection).UseSnakeCaseNamingConvention().Options);
}
