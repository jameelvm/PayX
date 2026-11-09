using PayX.Contracts;
using PayX.Ledger.Domain;
using PayX.Ledger.Repositories;

namespace PayX.Ledger.Services;

/// <summary>What to book: the entry's facts and its legs, accounts named by key.</summary>
public sealed record EntryDraft(EntryKind Kind, string PaymentId, Guid CustomerId, Guid MerchantId, Money Amount,
    DateTimeOffset OccurredAt, IReadOnlyList<Leg> Legs);

/// <summary>The one way anything is written to the books.</summary>
public sealed class LedgerWriter(LedgerDbContext db, AccountRepository accounts, TimeProvider clock)
{
    public async Task<JournalEntry> PostAsync(EntryDraft draft, CancellationToken ct)
    {
        // Refuse an unbalanced entry before touching the database at all.
        JournalEntry.EnsureBalanced(draft.Legs);

        var byKey = await accounts.GetOrCreateAsync(draft.Legs.Select(l => l.Account), ct);
        var entry = new JournalEntry(draft.Kind, draft.PaymentId, draft.CustomerId, draft.MerchantId, draft.Amount,
            draft.OccurredAt, clock.GetUtcNow(), draft.Legs, byKey);

        db.JournalEntries.Add(entry);
        // One SaveChanges = one transaction: the entry and all its postings
        // commit together, and the balance trigger checks them at commit.
        await db.SaveChangesAsync(ct);
        return entry;
    }
}
