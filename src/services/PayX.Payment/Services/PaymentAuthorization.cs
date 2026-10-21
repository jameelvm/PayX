using Microsoft.EntityFrameworkCore;
using PayX.Contracts;
using PayX.Payment.Domain;
using PayX.Payment.Repositories;

namespace PayX.Payment.Services;

/// <param name="Failure">Set when the payment couldn't reach a business
/// outcome (Authorized, Declined, RiskBlocked); the payment is left where it got to.</param>
public sealed record AuthorizationResult(Domain.Payment Payment, int FailureStatus = 0, string? Failure = null);

/// <summary>
/// The doc's <c>authorizePayment</c>, orchestrated (DESIGN.md decision 3):
/// Payment drives each step and records each outcome in the payment's state,
/// so at any moment one row says how far the payment got.
///
/// <code>
/// save Created ─► risk ─► save RiskApproved ─► Vault → PSP ─► save Authorized / Declined
///                   └──► save RiskBlocked (PSP never called)
/// </code>
///
/// Resumable (4.6): each step runs only if the payment is in the state that
/// step starts from. A retry that resumes a payment left at RiskApproved
/// therefore skips straight to the Vault call, with the same PSP key, so the
/// PSP replays what it did rather than authorizing twice.
///
/// When Vault or the PSP fails or times out, the payment stays RiskApproved
/// and the caller gets an error; deciding what that means without a retry
/// (the UNKNOWN state, the resolver) is Phase 6.
/// </summary>
public sealed class PaymentAuthorization(PaymentsDbContext db, IRiskCheck risk, VaultClient vault,
    CurrentIdempotencyKey key)
{
    public async Task<AuthorizationResult> AuthorizeAsync(Guid customerId, Guid merchantId, Money amount,
        string cardToken, CancellationToken ct)
    {
        var payment = await ResumeOrCreateAsync(customerId, merchantId, amount, cardToken, ct);

        // Risk first: a blocked payment never reaches the PSP.
        if (payment.Status == PaymentStatus.Created)
        {
            if (await risk.CheckAsync(payment, ct) == RiskDecision.Block)
                payment.BlockRisk();
            else
                payment.ApproveRisk();
            await db.SaveChangesAsync(ct);
        }

        // Ask for the money, by token. The key is fixed per payment and
        // operation, so asking again for this payment can never become a
        // second authorization at the PSP (decision 7; refined in 4.7).
        if (payment.Status == PaymentStatus.RiskApproved)
        {
            var result = await vault.AuthorizeAsync(payment.CardToken, payment.Amount, payment.MerchantId,
                $"{payment.Id}:authorize", ct);
            if (result.Authorization is not { } answer)
                return new AuthorizationResult(payment, result.StatusCode, result.Error);

            // A decline is an outcome, not an error.
            if (answer.Status == "authorized")
                payment.Authorize(answer.AuthorizationId, answer.AuthorizationCode!);
            else
                payment.Decline(answer.DeclineCode ?? "declined");
            await db.SaveChangesAsync(ct);
        }

        return new AuthorizationResult(payment);
    }

    private async Task<Domain.Payment> ResumeOrCreateAsync(Guid customerId, Guid merchantId, Money amount,
        string cardToken, CancellationToken ct)
    {
        // A retry that took over a key whose payment already exists continues
        // that payment. (The request hash guarantees it's the same request.)
        if (key.Record?.PaymentId is { } existingId)
            return await db.Payments.SingleAsync(p => p.Id == existingId, ct);

        // Write it down before talking to anyone. If the process dies at any
        // later step, the row shows a payment was attempted and how far it
        // got, instead of money moving with no record on our side.
        var payment = new Domain.Payment(merchantId, customerId, amount, cardToken);
        db.Payments.Add(payment);
        // Recovery point: the key learns its payment in the same transaction
        // the payment is created in. Both exist, or neither does.
        key.Record?.AttachPayment(payment.Id);
        await db.SaveChangesAsync(ct);
        return payment;
    }
}
