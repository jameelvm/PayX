namespace PayX.PspSimulator.Domain;

/// <summary>
/// One authorization request and the issuer's answer, remembered.
///
/// An approved authorization is a **hold**: the money is not moved, but it
/// is reserved, so the cardholder's available credit drops by this amount
/// straight away (doc ch.1: "Successful authorization reserves the amount on
/// the customer's card but does not immediately transfer funds"). Capture
/// (3.3) turns a hold into a charge; void (3.3) releases it.
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

public enum AuthorizationStatus
{
    Authorized,   // a live hold on the cardholder's credit
    Declined,     // the issuer said no; nothing held
}
