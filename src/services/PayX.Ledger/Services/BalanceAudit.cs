using Microsoft.EntityFrameworkCore;
using PayX.Ledger.Repositories;

namespace PayX.Ledger.Services;

public sealed record BalanceMismatch(long AccountId, long Stored, long FromPostings);
public sealed record CurrencyTotal(string Currency, long Total);

/// <summary>
/// "Never trust a single record" (7.5): two checks that the stored balances
/// tell the truth. Cheap enough to run on demand, and the starting point
/// for reconciliation (Phase 8), which then compares the ledger with the
/// PSP's own records.
/// </summary>
public sealed class BalanceAudit(LedgerDbContext db)
{
    /// <summary>
    /// Accounts whose stored balance differs from the sum of their postings.
    /// Always empty while balances only change next to their postings (7.5);
    /// a row here means something wrote one without the other.
    /// </summary>
    public Task<List<BalanceMismatch>> FindMismatchesAsync(CancellationToken ct) =>
        db.Database.SqlQuery<BalanceMismatch>($"""
            SELECT a.id AS account_id, a.balance_minor AS stored, coalesce(sum(p.amount_minor), 0)::bigint AS from_postings
            FROM accounts a LEFT JOIN postings p ON p.account_id = a.id
            GROUP BY a.id, a.balance_minor
            HAVING a.balance_minor <> coalesce(sum(p.amount_minor), 0)
            """).ToListAsync(ct);

    /// <summary>
    /// The trial balance: per currency, every account's balance added up.
    /// Double entry means each entry sums to zero, so the whole ledger does
    /// too; any non-zero total is money created or destroyed somewhere.
    /// </summary>
    public Task<List<CurrencyTotal>> TrialBalanceAsync(CancellationToken ct) =>
        db.Database.SqlQuery<CurrencyTotal>($"""
            SELECT currency, sum(balance_minor)::bigint AS total FROM accounts GROUP BY currency ORDER BY currency
            """).ToListAsync(ct);
}
