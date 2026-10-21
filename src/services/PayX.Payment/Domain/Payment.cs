using PayX.Contracts;

namespace PayX.Payment.Domain;

/// <summary>
/// One customer payment to one merchant: PayX's own record of it, as opposed
/// to the PSP's authorization (which is the other side's record, Phase 3).
/// Status only changes through the methods below, and each one asks
/// <see cref="PaymentStateMachine"/> first. There is no public setter: code
/// that wants a payment "Captured" has to capture it.
/// </summary>
public sealed class Payment
{
    public string Id { get; private set; } = null!;          // "pay_…"
    public Guid MerchantId { get; private set; }               // Identity user ids
    public Guid CustomerId { get; private set; }
    public Money Amount { get; private set; } = null!;
    public string CardToken { get; private set; } = null!;     // Vault token: never a card number
    public PaymentStatus Status { get; private set; }

    public string? PspAuthorizationId { get; private set; }    // the PSP's "auth_…": capture and void quote it
    public string? AuthorizationCode { get; private set; }     // the issuer's proof it said yes
    public string? DeclineCode { get; private set; }
    public long? CapturedAmountMinor { get; private set; }     // same currency as Amount; may be less

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Postgres <c>xmin</c>, as on the PSP side (3.3): two workers that load
    /// the same Authorized payment and try different moves (capture vs void)
    /// can't both save; the second UPDATE matches no row.
    /// </summary>
    public uint Version { get; private set; }

    public Payment(Guid merchantId, Guid customerId, Money amount, string cardToken)
    {
        if (amount.AmountMinor <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "A payment must be for a positive amount");

        Id = $"pay_{Guid.CreateVersion7():N}";
        MerchantId = merchantId;
        CustomerId = customerId;
        Amount = amount;
        CardToken = cardToken;
        Status = PaymentStatus.Created;
        CreatedAt = UpdatedAt = DateTimeOffset.UtcNow;
    }

    private Payment() { }   // EF Core

    public void ApproveRisk() => MoveTo(PaymentStatus.RiskApproved);

    public void BlockRisk() => MoveTo(PaymentStatus.RiskBlocked);

    public void Authorize(string pspAuthorizationId, string authorizationCode)
    {
        MoveTo(PaymentStatus.Authorized);
        PspAuthorizationId = pspAuthorizationId;
        AuthorizationCode = authorizationCode;
    }

    public void Decline(string declineCode)
    {
        MoveTo(PaymentStatus.Declined);
        DeclineCode = declineCode;
    }

    /// <summary>
    /// Why this capture would be refused, or null if it's fine. Asked
    /// <em>before</em> calling the PSP (4.3): a capture the PSP performs but
    /// we then refuse to record would be money moved with no record of it.
    /// </summary>
    public string? CaptureRefusal(Money amount)
    {
        if (!PaymentStateMachine.CanMove(Status, PaymentStatus.Captured)) return "payment_not_capturable";
        if (amount.Currency != Amount.Currency) return "currency_mismatch";
        if (amount.AmountMinor <= 0 || amount.AmountMinor > Amount.AmountMinor) return "amount_exceeds_authorized";
        return null;
    }

    /// <summary>Partial capture allowed (ship 2 of 3 items), never more than authorized.</summary>
    public void Capture(Money amount)
    {
        EnsureCanMoveTo(PaymentStatus.Captured);
        if (CaptureRefusal(amount) is { } refusal)
            throw new ArgumentException($"Capture refused: {refusal}", nameof(amount));

        MoveTo(PaymentStatus.Captured);
        CapturedAmountMinor = amount.AmountMinor;
    }

    public void Void() => MoveTo(PaymentStatus.Voided);

    private void EnsureCanMoveTo(PaymentStatus next)
    {
        if (!PaymentStateMachine.CanMove(Status, next))
            throw new IllegalTransitionException(Id, Status, next);
    }

    private void MoveTo(PaymentStatus next)
    {
        EnsureCanMoveTo(next);
        Status = next;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
