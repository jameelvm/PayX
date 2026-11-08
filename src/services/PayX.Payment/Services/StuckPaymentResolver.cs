using Microsoft.EntityFrameworkCore;
using PayX.Payment.Domain;
using PayX.Payment.Repositories;

namespace PayX.Payment.Services;

/// <summary>
/// Doc ch.2, transient failures: a payment whose authorization got no
/// answer and that nobody came back for (6.5; DESIGN decision 8). The
/// customer closed the tab, the process died mid-call, or the bank timed
/// out: the payment sits at <c>Unknown</c> or <c>RiskApproved</c> while a
/// hold may exist on the card. Only the PSP knows, so the resolver asks it,
/// by the reference sent with the authorization, and records its answer.
/// </summary>
public sealed class StuckPaymentResolver(PaymentsDbContext db, PspClient psp, TimeProvider clock,
    ILogger<StuckPaymentResolver> logger)
{
    /// <summary>
    /// Payments left at Unknown or RiskApproved for longer than
    /// <paramref name="grace"/>. The grace period must outlast any request
    /// still in flight (Vault gives up after 10 s, Payment after 15 s, an
    /// idempotency lock lasts 30 s): a younger payment may still get its
    /// answer the normal way, and must not be raced.
    /// </summary>
    public Task<List<string>> FindStuckAsync(TimeSpan grace, int limit, CancellationToken ct)
    {
        var before = clock.GetUtcNow() - grace;
        return db.Payments
            .Where(p => (p.Status == PaymentStatus.Unknown || p.Status == PaymentStatus.RiskApproved) && p.UpdatedAt < before)
            .OrderBy(p => p.UpdatedAt).Take(limit).Select(p => p.Id).ToListAsync(ct);
    }

    /// <returns>The status the payment ended in, or null when nothing could be concluded this time.</returns>
    public async Task<PaymentStatus?> ResolveAsync(string paymentId, CancellationToken ct)
    {
        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId, ct);
        if (payment.Status is not (PaymentStatus.Unknown or PaymentStatus.RiskApproved))
            return payment.Status;   // resolved meanwhile (a retry got there first)

        var lookup = await psp.FindByReferenceAsync(payment.Id, ct);
        if (!lookup.Reachable)
            return null;   // PSP unreachable: no answer is not "no record". Try again next pass.

        switch (lookup.Authorization)
        {
            case { Status: "declined" } declined:
                payment.Decline(declined.DeclineCode ?? "declined");
                break;
            case { } held:   // authorized: the only other state an uncaptured hold can be in
                payment.Authorize(held.AuthorizationId, held.AuthorizationCode!);
                break;
            default:
                // The PSP never processed it: no hold exists, and none will.
                // Nothing is in flight after the grace period, and Failed is
                // final, so a late retry never reaches the bank.
                payment.Fail();
                break;
        }

        try
        {
            await db.SaveChangesAsync(ct);   // the payment.<status> event goes out via the outbox
        }
        catch (DbUpdateConcurrencyException)
        {
            // The customer's retry resolved it in the same instant (xmin).
            // Theirs stands; it came from the same PSP answer.
            logger.LogInformation("Payment {PaymentId} was resolved concurrently", payment.Id);
            return null;
        }

        logger.LogWarning("Resolved stuck payment {PaymentId}: {Status}", payment.Id, payment.Status);
        return payment.Status;
    }
}
