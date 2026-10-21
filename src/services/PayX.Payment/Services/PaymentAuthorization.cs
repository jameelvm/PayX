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
/// Happy path only. When Vault or the PSP fails or times out, the payment
/// stays RiskApproved and the caller gets an error: deciding what that means
/// (the UNKNOWN state, the resolver) is Phase 6.
/// </summary>
public sealed class PaymentAuthorization(PaymentsDbContext db, IRiskCheck risk, VaultClient vault)
{
    public async Task<AuthorizationResult> AuthorizeAsync(Guid customerId, Guid merchantId, Money amount,
        string cardToken, CancellationToken ct)
    {
        // 1. Write it down before talking to anyone. If the process dies at
        //    any later step, the row shows a payment was attempted and how far
        //    it got, instead of money moving with no record on our side.
        var payment = new Domain.Payment(merchantId, customerId, amount, cardToken);
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);

        // 2. Risk first: a blocked payment never reaches the PSP.
        if (await risk.CheckAsync(payment, ct) == RiskDecision.Block)
        {
            payment.BlockRisk();
            await db.SaveChangesAsync(ct);
            return new AuthorizationResult(payment);
        }
        payment.ApproveRisk();
        await db.SaveChangesAsync(ct);

        // 3. Ask for the money, by token. The key is fixed per payment and
        //    operation, so asking again for this payment can never become a
        //    second authorization at the PSP (decision 7; refined in 4.7).
        var result = await vault.AuthorizeAsync(cardToken, amount, merchantId, $"{payment.Id}:authorize", ct);
        if (result.Authorization is not { } answer)
            return new AuthorizationResult(payment, result.StatusCode, result.Error);

        // 4. Record the PSP's answer. A decline is an outcome, not an error.
        if (answer.Status == "authorized")
            payment.Authorize(answer.AuthorizationId, answer.AuthorizationCode!);
        else
            payment.Decline(answer.DeclineCode ?? "declined");
        await db.SaveChangesAsync(ct);

        return new AuthorizationResult(payment);
    }
}
