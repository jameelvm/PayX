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
    /// <summary>
    /// The accounts, created if new, <b>locked</b> (<c>FOR UPDATE</c>) until
    /// the caller's transaction ends (7.5). Must run inside a transaction.
    ///
    /// <para>The lock is what makes "balance += amount" safe with several
    /// consumers: a second booking on the same account waits for the first
    /// to commit, then reads the balance it left. Without it both read 100,
    /// both write 100 + their amount, and one booking vanishes from the
    /// balance (a lost update) while its postings stay.</para>
    ///
    /// <para>Locked in id order, always: two bookings that touch the same
    /// two accounts then queue instead of each holding one and waiting for
    /// the other (a deadlock).</para>
    /// </summary>
    public async Task<Dictionary<AccountKey, Account>> LockAsync(IEnumerable<AccountKey> keys, CancellationToken ct)
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

        var ids = new List<long>();
        foreach (var key in wanted)
            ids.Add(await db.Accounts.AsNoTracking()
                .Where(a => a.Type == key.Type && a.OwnerId == key.OwnerId && a.Currency == key.Currency)
                .Select(a => a.Id).SingleAsync(ct));

        // EF hands back an already-tracked entity as it is, without the
        // values it just read: a context that loaded this account earlier
        // would keep a stale balance even under the lock. Forget them first.
        foreach (var tracked in db.ChangeTracker.Entries<Account>().Where(e => ids.Contains(e.Entity.Id)).ToList())
            tracked.State = EntityState.Detached;

        var locked = await db.Accounts
            .FromSql($"SELECT * FROM accounts WHERE id = ANY({ids.ToArray()}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
        return locked.ToDictionary(a => a.Key);
    }
}
