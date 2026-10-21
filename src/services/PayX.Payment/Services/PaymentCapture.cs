using Microsoft.EntityFrameworkCore;
using PayX.Contracts;
using PayX.Payment.Repositories;

namespace PayX.Payment.Services;

/// <param name="Payment">Null when there is no such payment for this merchant.</param>
public sealed record CaptureResult(Domain.Payment? Payment, int FailureStatus = 0, string? Failure = null);

/// <summary>
/// The doc's <c>capturePayment</c>, the second half of the two-phase card
/// flow: the merchant (typically on shipping) turns the hold into a charge.
/// Partial capture is allowed; the rest of the hold is released by the PSP.
/// </summary>
public sealed class PaymentCapture(PaymentsDbContext db, PspClient psp, CurrentIdempotencyKey key)
{
    public async Task<CaptureResult> CaptureAsync(string paymentId, Guid merchantId, long? amountMinor, CancellationToken ct)
    {
        // Another merchant's payment looks exactly like a missing one: no
        // hint that the id exists.
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == paymentId && p.MerchantId == merchantId, ct);
        if (payment is null)
            return new CaptureResult(null, 404, "payment_not_found");

        // No amount = capture everything that was authorized.
        var amount = new Money(amountMinor ?? payment.Amount.AmountMinor, payment.Amount.Currency);

        // A resumed key (4.6) whose earlier attempt saved Captured but died
        // before storing its answer: that capture is this request's outcome,
        // not a conflict. (A *new* key on a captured payment is still 409.)
        if (key.Resumed && payment.Status == Domain.PaymentStatus.Captured && payment.CapturedAmountMinor == amount.AmountMinor)
            return new CaptureResult(payment);

        // 1. Refuse here what we couldn't record afterwards: the PSP is never
        //    asked to capture a payment we'd then fail to mark Captured.
        if (payment.CaptureRefusal(amount) is { } refusal)
            return new CaptureResult(payment, refusal == "payment_not_capturable" ? 409 : 422, refusal);

        // 2. Ask the PSP, quoting its own authorization id. One key per
        //    payment and operation: a retry (same amount) replays the PSP's
        //    first answer instead of capturing twice; a retry with a different
        //    amount is refused by the PSP (key reused for a different request).
        var result = await psp.CaptureAsync(payment.PspAuthorizationId!, amount.AmountMinor, $"{payment.Id}:capture", ct);
        if (result.Status != "captured")
            // Refused (409 from the PSP) or no answer (502/504): the payment
            // stays Authorized. After a timeout, repeating the same capture is safe.
            return new CaptureResult(payment, result.StatusCode is >= 500 ? result.StatusCode : 409,
                result.Error ?? $"psp_status_{result.Status}");

        // 3. Record it.
        payment.Capture(amount);
        await db.SaveChangesAsync(ct);
        return new CaptureResult(payment);
    }
}
