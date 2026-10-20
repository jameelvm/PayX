using Microsoft.EntityFrameworkCore;
using PayX.PspSimulator.Domain;
using PayX.PspSimulator.Repositories;

namespace PayX.PspSimulator.Services;

/// <summary>What happened to a capture or void request.</summary>
/// <param name="Error">null on success, else a refusal code ("authorization_not_capturable"…).</param>
public sealed record ActionOutcome(Authorization? Authorization, string? Error, bool NotFound = false);

/// <summary>
/// Capture and void: the second phase of a card payment (doc ch.1, settlement
/// phase; doc ch.2, <c>capturePayment</c>).
/// </summary>
public sealed class AuthorizationActions(PspDbContext db)
{
    public Task<ActionOutcome> CaptureAsync(string id, long amountMinor, CancellationToken ct) =>
        ApplyAsync(id, a => a.Capture(amountMinor), ct);

    public Task<ActionOutcome> VoidAsync(string id, CancellationToken ct) =>
        ApplyAsync(id, a => a.Void(), ct);

    private async Task<ActionOutcome> ApplyAsync(string id, Func<Authorization, string?> action, CancellationToken ct)
    {
        var authorization = await db.Authorizations.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (authorization is null)
            return new ActionOutcome(null, null, NotFound: true);

        // The state rules live on the entity: capture only from Authorized,
        // never more than authorized, void only from Authorized.
        if (action(authorization) is { } refusal)
            return new ActionOutcome(authorization, refusal);

        try
        {
            await db.SaveChangesAsync(ct);
            return new ActionOutcome(authorization, null);
        }
        // Optimistic concurrency, where Module 3.2 used a lock. A capture and
        // a void for the same authorization racing each other would both
        // load it as Authorized, and both pass the state check above. The
        // first UPDATE wins; the second finds xmin changed, updates nothing,
        // and lands here. No lock was needed, because two requests for one
        // authorization are rare; a card account receiving many holds at once
        // (3.2) is the opposite case, where waiting beats failing and retrying.
        catch (DbUpdateConcurrencyException)
        {
            // Forget our losing change, or the next SaveChanges in this request
            // (the idempotency filter storing this 409) would try to write it
            // again and fail the same way.
            db.Entry(authorization).State = EntityState.Detached;
            return new ActionOutcome(authorization, "authorization_changed_concurrently");
        }
    }
}
