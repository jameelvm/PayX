using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayX.Contracts;
using PayX.Ledger.Domain;
using PayX.Ledger.Repositories;

namespace PayX.Ledger.Services;

/// <summary>What to book: the entry's facts and its legs, accounts named by key, and the event that caused it.</summary>
public sealed record EntryDraft(EntryKind Kind, string PaymentId, Guid CustomerId, Guid MerchantId, Money Amount,
    DateTimeOffset OccurredAt, IReadOnlyList<Leg> Legs, Guid SourceEventId);

public enum PostOutcome
{
    Posted,
    /// <summary>This exact event was booked before (a redelivery): nothing done.</summary>
    Duplicate,
    /// <summary>This payment step is already booked, under another event id: nothing done.</summary>
    StepAlreadyBooked,
}

public sealed record PostResult(PostOutcome Outcome, JournalEntry? Entry);

/// <summary>The one way anything is written to the books.</summary>
public sealed class LedgerWriter(LedgerDbContext db, AccountRepository accounts, TimeProvider clock)
{
    public async Task<PostResult> PostAsync(EntryDraft draft, CancellationToken ct)
    {
        // Refuse an unbalanced entry before touching the database at all.
        JournalEntry.EnsureBalanced(draft.Legs);

        // Fast path for a redelivery (7.4): the event is already in the books.
        if (await db.ProcessedEvents.AnyAsync(p => p.EventId == draft.SourceEventId, ct))
            return new PostResult(PostOutcome.Duplicate, null);

        var byKey = await accounts.GetOrCreateAsync(draft.Legs.Select(l => l.Account), ct);
        var now = clock.GetUtcNow();
        var entry = new JournalEntry(draft.Kind, draft.PaymentId, draft.CustomerId, draft.MerchantId, draft.Amount,
            draft.OccurredAt, now, draft.Legs, byKey);

        db.JournalEntries.Add(entry);
        db.ProcessedEvents.Add(new ProcessedEvent(draft.SourceEventId, draft.PaymentId, entry, now));
        try
        {
            // One SaveChanges = one transaction: the entry, its postings and
            // the "processed" mark commit together or not at all. Booked but
            // not marked (or marked but not booked) can't happen.
            await db.SaveChangesAsync(ct);
            return new PostResult(PostOutcome.Posted, entry);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
                                           && pg.ConstraintName is "pk_processed_events" or LedgerDbContext.OneEntryPerStep)
        {
            // Lost a race to another delivery of the same thing, which
            // committed first: its booking stands, and this one rolled back whole.
            db.ChangeTracker.Clear();
            return new PostResult(
                pg.ConstraintName == "pk_processed_events" ? PostOutcome.Duplicate : PostOutcome.StepAlreadyBooked, null);
        }
    }
}
