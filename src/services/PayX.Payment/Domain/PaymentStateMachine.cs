namespace PayX.Payment.Domain;

/// <summary>
/// Where a payment is in its life. Stored as text, so a row read in psql says
/// "Authorized", not "4". More states arrive with the modules that need them:
/// Challenged (5.3), Unknown (6.4), Settled (8), Refunded and Disputed (9).
/// </summary>
public enum PaymentStatus
{
    Created,        // accepted from the customer, nothing checked yet
    RiskApproved,   // Risk said allow: safe to ask the PSP
    RiskBlocked,    // Risk said block: the PSP is never called
    Authorized,     // PSP/issuer reserved the amount on the card (a hold)
    Declined,       // issuer said no; nothing held
    Captured,       // money moved (all or part of the hold)
    Voided,         // hold released, nothing charged
}

/// <summary>
/// The legal moves, in one table (DESIGN.md decision 3: an orchestrated saga
/// with an explicit state machine). PaymentService drives every step, and
/// every step must be a move listed here; anything else is a bug or a race,
/// never something to "fix up" by writing a status directly.
///
/// <code>
/// Created ──► RiskApproved ──► Authorized ──► Captured
///    │             │               └────────► Voided
///    └► RiskBlocked └► Declined
/// </code>
/// </summary>
public static class PaymentStateMachine
{
    private static readonly Dictionary<PaymentStatus, PaymentStatus[]> Moves = new()
    {
        [PaymentStatus.Created] = [PaymentStatus.RiskApproved, PaymentStatus.RiskBlocked],
        [PaymentStatus.RiskApproved] = [PaymentStatus.Authorized, PaymentStatus.Declined],
        [PaymentStatus.Authorized] = [PaymentStatus.Captured, PaymentStatus.Voided],
        // Not listed = final: RiskBlocked, Declined, Captured, Voided. Nothing
        // ever goes backwards (a captured payment is undone by a refund, a new
        // movement, in Phase 9; never by moving it back to Authorized).
    };

    public static bool CanMove(PaymentStatus from, PaymentStatus to) =>
        Moves.TryGetValue(from, out var next) && next.Contains(to);

    public static bool IsFinal(PaymentStatus status) => !Moves.ContainsKey(status);
}

public sealed class IllegalTransitionException(string paymentId, PaymentStatus from, PaymentStatus to)
    : InvalidOperationException($"Payment {paymentId} cannot move from {from} to {to}")
{
    public PaymentStatus From { get; } = from;
    public PaymentStatus To { get; } = to;
}
