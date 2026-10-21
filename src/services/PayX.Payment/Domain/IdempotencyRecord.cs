namespace PayX.Payment.Domain;

/// <summary>
/// One <c>Idempotency-Key</c> a caller sent to a money-moving endpoint, and
/// what we answered (DESIGN.md decision 6). A retry with the same key gets the
/// stored answer back instead of running the request again: the doc's answer
/// to "the customer hits Buy twice".
///
/// Unlike the PSP's version (3.4), this one can't be claimed, worked and
/// completed inside one database transaction: the work calls Vault and the
/// PSP, and a transaction must not stay open across network calls
/// (decision 31). So the claim is committed on its own first, and the row
/// says "in progress" (no response yet) until the request finishes.
/// </summary>
public sealed class IdempotencyRecord
{
    /// <summary>Whose key: the caller's user id. Two customers may both send "abc" without colliding.</summary>
    public string Scope { get; private set; } = null!;
    public string Key { get; private set; } = null!;

    /// <summary>SHA-256 of method, path and arguments: the same key must mean the same request.</summary>
    public string RequestHash { get; private set; } = null!;

    /// <summary>
    /// Who is working on it, until when (4.6). A live lock means "a request is
    /// running": duplicates get 409. An expired or released one means "the
    /// last attempt died or failed": the next retry may take over.
    /// </summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>
    /// Recovery point (4.6): the payment this key created, saved in the same
    /// transaction as the payment itself. A retry that takes over the key
    /// resumes this payment rather than creating a second one, which would
    /// get its own PSP key and could be authorized a second time.
    /// </summary>
    public string? PaymentId { get; private set; }

    public int? ResponseStatus { get; private set; }
    public string? ResponseBody { get; private set; }
    public string? ResponseLocation { get; private set; }   // a replayed 201 points where the first one did

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsCompleted => CompletedAt is not null;

    public IdempotencyRecord(string scope, string key, string requestHash, DateTimeOffset lockedUntil)
    {
        Scope = scope;
        Key = key;
        RequestHash = requestHash;
        LockedUntil = lockedUntil;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private IdempotencyRecord() { }   // EF Core

    public void AttachPayment(string paymentId) => PaymentId = paymentId;

    public void Complete(int status, string body, string? location)
    {
        ResponseStatus = status;
        ResponseBody = body;
        ResponseLocation = location;
        CompletedAt = DateTimeOffset.UtcNow;
        LockedUntil = null;
    }

    /// <summary>No outcome yet (a 5xx): let the next retry take over straight away.</summary>
    public void Release() => LockedUntil = null;
}
