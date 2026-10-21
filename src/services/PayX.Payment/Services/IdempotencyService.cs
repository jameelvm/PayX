using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayX.Payment.Domain;
using PayX.Payment.Repositories;

namespace PayX.Payment.Services;

public enum ClaimOutcome
{
    Claimed,      // first time: run the request
    Replay,       // done before: return the stored answer
    Mismatch,     // same key, different request: a client bug
    InProgress,   // the first request with this key hasn't finished
}

public sealed record Claim(ClaimOutcome Outcome, IdempotencyRecord? Record);

/// <summary>
/// Claim a key, or find out what happened to it before. Used by the
/// <c>[Idempotent]</c> filter around Payment's money-moving endpoints.
/// </summary>
public sealed class IdempotencyService(PaymentsDbContext db)
{
    public async Task<Claim> ClaimAsync(string scope, string key, string requestHash, CancellationToken ct)
    {
        // Insert first, ask questions after: the primary key decides who was
        // first. A "does it exist?" check before inserting would let two
        // simultaneous requests both see "no" (2.2's lesson).
        var record = new IdempotencyRecord(scope, key, requestHash);
        db.IdempotencyRecords.Add(record);
        try
        {
            // Committed on its own, before the work starts: from now on any
            // duplicate sees this key, even while the work is still running.
            await db.SaveChangesAsync(ct);
            return new Claim(ClaimOutcome.Claimed, record);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(record).State = EntityState.Detached;
        }

        var existing = await db.IdempotencyRecords.AsNoTracking()
            .SingleAsync(r => r.Scope == scope && r.Key == key, ct);
        return existing switch
        {
            { RequestHash: var hash } when hash != requestHash => new Claim(ClaimOutcome.Mismatch, existing),
            { IsCompleted: true } => new Claim(ClaimOutcome.Replay, existing),
            _ => new Claim(ClaimOutcome.InProgress, existing),
        };
    }

    public async Task CompleteAsync(IdempotencyRecord record, int status, string body, string? location, CancellationToken ct)
    {
        record.Complete(status, body, location);
        await db.SaveChangesAsync(ct);
    }
}
