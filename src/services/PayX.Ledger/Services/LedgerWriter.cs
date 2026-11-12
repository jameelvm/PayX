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
    /// <summary>
    /// Books one entry. Everything below happens in <b>one</b> database
    /// transaction (DESIGN decision 10, a deliberate deviation from the doc):
    ///
    /// <code>
    ///   lock the accounts ─► insert entry + postings ─► balances += postings ─► mark event processed ─► COMMIT
    /// </code>
    ///
    /// The doc updates the wallet, then appends to the ledger: two writes,
    /// and a crash between them leaves a balance that disagrees with its
    /// own history, with nothing to say which is right. Here the balance and
    /// the postings it sums can only commit together, so the balance is a
    /// provably consistent projection, and reading it is one row, not a SUM
    /// over every posting the account ever had.
    /// </summary>
    public async Task<PostResult> PostAsync(EntryDraft draft, CancellationToken ct)
    {
        // Refuse an unbalanced entry before touching the database at all.
        JournalEntry.EnsureBalanced(draft.Legs);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Fast path for a redelivery (7.4): the event is already in the books.
        if (await db.ProcessedEvents.AnyAsync(p => p.EventId == draft.SourceEventId, ct))
            return new PostResult(PostOutcome.Duplicate, null);

        var byKey = await accounts.LockAsync(draft.Legs.Select(l => l.Account), ct);
        var now = clock.GetUtcNow();
        var entry = new JournalEntry(draft.Kind, draft.PaymentId, draft.CustomerId, draft.MerchantId, draft.Amount,
            draft.OccurredAt, now, draft.Legs, byKey);
        foreach (var leg in draft.Legs)
            byKey[leg.Account].Apply(leg.AmountMinor);   // the wallet moves with its postings (7.5)

        db.JournalEntries.Add(entry);
        db.ProcessedEvents.Add(new ProcessedEvent(draft.SourceEventId, draft.PaymentId, entry, now));
        try
        {
            await db.SaveChangesAsync(ct);
            // Postgres checks the deferred double-entry trigger here (7.1).
            await tx.CommitAsync(ct);
            return new PostResult(PostOutcome.Posted, entry);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
                                           && pg.ConstraintName is "pk_processed_events" or LedgerDbContext.OneEntryPerStep)
        {
            // Lost a race to another delivery of the same thing, which
            // committed first: its booking stands, and this one rolls back
            // whole, balances included (7.4).
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return new PostResult(
                pg.ConstraintName == "pk_processed_events" ? PostOutcome.Duplicate : PostOutcome.StepAlreadyBooked, null);
        }
    }
}
