using Microsoft.EntityFrameworkCore;
using PayX.Payment.Repositories;

namespace PayX.Payment.Services;

/// <summary>
/// The doc's <c>checkPaymentStatus(payment_id)</c>: PayX's own record of where
/// a payment is. It reads our row and never calls the PSP: asking the PSP what
/// really happened to an unresolved payment is the resolver's job (6.5).
/// </summary>
public sealed class PaymentQueries(PaymentsDbContext db)
{
    /// <summary>
    /// The payment, if the caller is its customer or its merchant. Anyone else
    /// gets null, exactly like a payment that doesn't exist: no hint that the
    /// id is real.
    /// </summary>
    public Task<Domain.Payment?> FindVisibleAsync(string paymentId, Guid callerId, CancellationToken ct) =>
        db.Payments.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == paymentId && (p.CustomerId == callerId || p.MerchantId == callerId), ct);
}
