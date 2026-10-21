using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayX.Payment.Domain;
using PayX.Payment.Repositories;

namespace PayX.Payment.Services;

public enum ClaimOutcome
{
    Claimed,      // first time: run the request
    Resumed,      // an earlier attempt died or failed: run it again, from where it got to (4.6)
    Replay,       // done before: return the stored answer
    Mismatch,     // same key, different request: a client bug
    InProgress,   // another request with this key is running right now
}

public sealed record Claim(ClaimOutcome Outcome, IdempotencyRecord? Record);

/// <summary>
/// The key the current request claimed, for the services behind the
/// endpoint: authorize attaches its new payment to it (the recovery point)
/// and resumes the payment already attached. Scoped: one per request.
/// </summary>
public sealed class CurrentIdempotencyKey
{
    public IdempotencyRecord? Record { get; set; }
    public bool Resumed { get; set; }
}

/// <summary>
/// Claim a key, or find out what happened to it before. Used by the
/// <c>[Idempotent]</c> filter around Payment's money-moving endpoints.
/// </summary>
public sealed class IdempotencyService(PaymentsDbContext db, IConfiguration config)
{
    // Longer than any request should take (Vault's own timeout is 15 s), so
    // a live request never loses its lock to a duplicate.
    private readonly TimeSpan _lock = config.GetValue("Payment:IdempotencyLock", TimeSpan.FromSeconds(30));

    public async Task<Claim> ClaimAsync(string scope, string key, string requestHash, CancellationToken ct)
    {
        // Insert first, ask questions after: the primary key decides who was
        // first. A "does it exist?" check before inserting would let two
        // simultaneous requests both see "no" (2.2's lesson).
        var record = new IdempotencyRecord(scope, key, requestHash, DateTimeOffset.UtcNow + _lock);
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
        if (existing.RequestHash != requestHash) return new Claim(ClaimOutcome.Mismatch, existing);
        if (existing.IsCompleted) return new Claim(ClaimOutcome.Replay, existing);

        // Not finished. If its lock has lapsed (the request died) or was
        // released (it ended without an outcome), take it over. One UPDATE
        // that only matches a lapsed lock: of several retries arriving
        // together, exactly one gets the row; the rest see a live lock.
        var now = DateTimeOffset.UtcNow;
        var takenOver = await db.IdempotencyRecords
            .Where(r => r.Scope == scope && r.Key == key && r.CompletedAt == null
                && (r.LockedUntil == null || r.LockedUntil < now))
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.LockedUntil, now + _lock), ct);
        if (takenOver == 0)
            return new Claim(ClaimOutcome.InProgress, existing);

        // Tracked from here, so the request can attach a payment or complete it.
        var resumed = await db.IdempotencyRecords.SingleAsync(r => r.Scope == scope && r.Key == key, ct);
        return new Claim(ClaimOutcome.Resumed, resumed);
    }

    public async Task CompleteAsync(IdempotencyRecord record, int status, string body, string? location, CancellationToken ct)
    {
        record.Complete(status, body, location);
        await db.SaveChangesAsync(ct);
    }

    public async Task ReleaseAsync(IdempotencyRecord record, CancellationToken ct)
    {
        record.Release();
        await db.SaveChangesAsync(ct);
    }
}
