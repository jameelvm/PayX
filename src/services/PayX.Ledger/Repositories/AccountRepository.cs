using Microsoft.EntityFrameworkCore;
using PayX.Ledger.Domain;

namespace PayX.Ledger.Repositories;

/// <summary>
/// Accounts are created on first use: the first payment to a merchant in a
/// currency opens that merchant's accounts in it. No registration step to
/// forget, and no account exists that nothing ever touched.
/// </summary>
public sealed class AccountRepository(LedgerDbContext db)
{
    public async Task<Dictionary<AccountKey, Account>> GetOrCreateAsync(IEnumerable<AccountKey> keys, CancellationToken ct)
    {
        var wanted = keys.Distinct().ToList();
        foreach (var key in wanted)
        {
            // ON CONFLICT DO NOTHING: two consumers opening the same account
            // at the same moment both succeed, and the unique index keeps one.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO accounts (type, owner_id, currency) VALUES ({key.Type.ToString()}, {key.OwnerId}, {key.Currency})
                ON CONFLICT (type, owner_id, currency) DO NOTHING
                """, ct);
        }

        var found = new Dictionary<AccountKey, Account>();
        foreach (var key in wanted)
            found[key] = await db.Accounts.SingleAsync(
                a => a.Type == key.Type && a.OwnerId == key.OwnerId && a.Currency == key.Currency, ct);
        return found;
    }
}
