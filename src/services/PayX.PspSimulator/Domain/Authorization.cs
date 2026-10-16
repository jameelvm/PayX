namespace PayX.PspSimulator.Domain;

/// <summary>
/// One authorization request and the issuer's answer, remembered.
///
/// An approved authorization is a **hold**: the money is not moved, but it
/// is reserved, so the cardholder's available credit drops by this amount
/// straight away (doc ch.1: "Successful authorization reserves the amount on
/// the customer's card but does not immediately transfer funds"). Capture
/// turns a hold into a charge; void releases it.
///
/// Declines are stored too: they hold nothing, but a merchant can still ask
/// what happened to an attempt (status lookup, 3.4).
/// </summary>
public sealed class Authorization
{
    public string Id { get; private set; } = null!;              // "auth_…", what the PSP gives back
    public string CardFingerprint { get; private set; } = null!;
    public string Last4 { get; private set; } = null!;
    public string Brand { get; private set; } = null!;
    public string MerchantId { get; private set; } = null!;
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = null!;
    public AuthorizationStatus Status { get; private set; }
    public string? AuthorizationCode { get; private set; }
    public string? DeclineCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>How much was actually charged. May be less than authorized.</summary>
    public long? CapturedAmountMinor { get; private set; }

    /// <summary>When the charge happened: settlement batches by this (Phase 8).</summary>
    public DateTimeOffset? CapturedAt { get; private set; }

    /// <summary>
    /// Concurrency token, mapped to Postgres's hidden <c>xmin</c> column (the
    /// id of the transaction that last wrote the row). EF adds
    /// "WHERE xmin = &lt;value I read&gt;" to every UPDATE, so if anyone changed
    /// the row since we loaded it, our UPDATE matches nothing and EF throws
    /// instead of silently overwriting their change.
    /// </summary>
    public uint Version { get; private set; }

    /// <summary>
    /// Doc ch.2, <c>capturePayment(authorization_code, amount)</c>: turn the hold
    /// into a charge. Partial capture is allowed (a shop that ships 2 of 3
    /// items charges for 2); the rest of the hold is released in the same step.
    /// </summary>
    /// <returns>null on success, otherwise why it was refused.</returns>
    public string? Capture(long amountMinor)
    {
        // Only a live hold can be captured: not a decline, not a voided hold,
        // and not one already captured (a second capture would charge twice).
        if (Status != AuthorizationStatus.Authorized) return "authorization_not_capturable";
        if (amountMinor > AmountMinor) return "amount_exceeds_authorized";

        Status = AuthorizationStatus.Captured;
        CapturedAmountMinor = amountMinor;
        CapturedAt = DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>Release the hold without charging anything (order cancelled).</summary>
    public string? Void()
    {
        // A charge can't be voided: once captured, money comes back only
        // through a refund (Phase 9), which is a new movement, not an undo.
        if (Status != AuthorizationStatus.Authorized) return "authorization_not_voidable";

        Status = AuthorizationStatus.Voided;
        return null;
    }

    public Authorization(string cardFingerprint, string last4, string brand, string merchantId,
        long amountMinor, string currency, string? declineCode, string? authorizationCode)
    {
        Id = $"auth_{Guid.CreateVersion7():N}";
        CardFingerprint = cardFingerprint;
        Last4 = last4;
        Brand = brand;
        MerchantId = merchantId;
        AmountMinor = amountMinor;
        Currency = currency;
        Status = declineCode is null ? AuthorizationStatus.Authorized : AuthorizationStatus.Declined;
        DeclineCode = declineCode;
        AuthorizationCode = authorizationCode;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private Authorization() { }   // EF Core
}

/// <summary>
/// Authorized ──capture──► Captured   (money charged)
///     └───────void─────► Voided     (hold released)
/// Declined, Captured and Voided are final.
/// </summary>
public enum AuthorizationStatus
{
    Authorized,   // a live hold on the cardholder's credit
    Declined,     // the issuer said no; nothing held
    Captured,     // charged: CapturedAmountMinor is owed on the card
    Voided,       // released: nothing held, nothing charged
}
