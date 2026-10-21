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

    public int? ResponseStatus { get; private set; }
    public string? ResponseBody { get; private set; }
    public string? ResponseLocation { get; private set; }   // a replayed 201 points where the first one did

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsCompleted => CompletedAt is not null;

    public IdempotencyRecord(string scope, string key, string requestHash)
    {
        Scope = scope;
        Key = key;
        RequestHash = requestHash;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private IdempotencyRecord() { }   // EF Core

    public void Complete(int status, string body, string? location)
    {
        ResponseStatus = status;
        ResponseBody = body;
        ResponseLocation = location;
        CompletedAt = DateTimeOffset.UtcNow;
    }
}
