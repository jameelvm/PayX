using Microsoft.EntityFrameworkCore;
using PayX.Contracts;
using PayX.Ledger.Domain;
using PayX.Ledger.Repositories;

namespace PayX.Ledger.Services;

/// <summary>Whose history: the customer who paid, or the merchant who was paid.</summary>
public enum Viewer { Customer, Merchant }

public sealed record HistoryItem(long EntryId, string PaymentId, EntryKind Kind, Money Amount,
    DateTimeOffset OccurredAt, DateTimeOffset RecordedAt);

/// <param name="NextBefore">Pass as <c>before</c> to get the next (older) page; null on the last page.</param>
public sealed record HistoryPage(IReadOnlyList<HistoryItem> Items, long? NextBefore);

/// <summary>
/// A merchant's money in one currency (7.7), in minor units, each never negative:
/// <list type="bullet">
/// <item><c>Authorized</c>: held on customers' cards, not captured yet. Not money yet: a void releases it.</item>
/// <item><c>Pending</c>: captured, so owed to the merchant, but the PSP hasn't settled it, so not payable yet.</item>
/// <item><c>Available</c>: settled, payable. Settlement moves pending here (Phase 8); until then it stays 0.</item>
/// </list>
/// </summary>
public sealed record MerchantBalance(string Currency, long Authorized, long Pending, long Available);

/// <summary>A customer's view (7.7): what is reserved on their card, per currency.</summary>
public sealed record CustomerBalance(string Currency, long OnHold);

/// <summary>
/// Reads for people (7.6, 7.7). Doc ch.1 FRs: "transaction history" and
/// "balance management". Read straight from the books, so what a customer
/// sees is what the ledger holds, not a copy that could disagree with it.
/// </summary>
public sealed class LedgerQueries(LedgerDbContext db)
{
    public const int MaxPageSize = 100;

    /// <summary>
    /// The caller's own entries, newest first. A caller only ever sees rows
    /// with their own id: there is no parameter to ask for someone else's.
    ///
    /// <para><b>Keyset paging</b> (<paramref name="before"/> = the last entry
    /// id seen), not OFFSET: page 500 costs the same index seek as page 1,
    /// and an entry booked while the user pages can't shift a row onto the
    /// next page twice or skip one. Entry ids only grow, so "older than X" is
    /// a stable cursor.</para>
    /// </summary>
    public async Task<HistoryPage> HistoryAsync(Guid callerId, Viewer viewer, DateTimeOffset? from, DateTimeOffset? to,
        long? before, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);

        var query = db.JournalEntries.AsNoTracking();
        query = viewer == Viewer.Customer
            ? query.Where(e => e.CustomerId == callerId)
            : query.Where(e => e.MerchantId == callerId);
        if (from is { } f) query = query.Where(e => e.OccurredAt >= f);
        if (to is { } t) query = query.Where(e => e.OccurredAt < t);
        if (before is { } b) query = query.Where(e => e.Id < b);

        // One extra row says whether another page exists, without a COUNT.
        var rows = await query.OrderByDescending(e => e.Id).Take(limit + 1)
            .Select(e => new HistoryItem(e.Id, e.PaymentId, e.Kind, e.Amount, e.OccurredAt, e.RecordedAt))
            .ToListAsync(ct);

        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        return new HistoryPage(rows, more ? rows[^1].EntryId : null);
    }

    /// <summary>
    /// Doc ch.1: balance management. Read from the stored balances (7.5):
    /// one row per account, whatever the history's length, and consistent
    /// with it by construction because both commit together. A currency the
    /// merchant has never been paid in doesn't appear.
    /// </summary>
    public async Task<IReadOnlyList<MerchantBalance>> MerchantBalancesAsync(Guid merchantId, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.OwnerId == merchantId && (a.Type == AccountType.MerchantAuthorized
                || a.Type == AccountType.MerchantPending || a.Type == AccountType.MerchantAvailable))
            .ToListAsync(ct);

        long Of(IEnumerable<Account> inCurrency, AccountType type) =>
            inCurrency.SingleOrDefault(a => a.Type == type)?.DisplayBalanceMinor ?? 0;

        return accounts.GroupBy(a => a.Currency).OrderBy(g => g.Key)
            .Select(g => new MerchantBalance(g.Key, Of(g, AccountType.MerchantAuthorized),
                Of(g, AccountType.MerchantPending), Of(g, AccountType.MerchantAvailable)))
            .ToList();
    }

    public async Task<IReadOnlyList<CustomerBalance>> CustomerBalancesAsync(Guid customerId, CancellationToken ct) =>
        (await db.Accounts.AsNoTracking()
            .Where(a => a.OwnerId == customerId && a.Type == AccountType.CustomerHolds)
            .OrderBy(a => a.Currency).ToListAsync(ct))
        .Select(a => new CustomerBalance(a.Currency, a.DisplayBalanceMinor)).ToList();
}
