namespace PayX.PspSimulator.Domain;

/// <summary>
/// "I have already handled the request with this key; here is the answer I
/// gave." Doc ch.2: "the client generate[s] a unique request ID (an
/// idempotency key) that the server uses to recognize and de-duplicate
/// retried requests."
///
/// The stored answer is what makes a retry safe: the caller gets the
/// original result back, byte for byte, instead of a second authorization
/// or a 409 "already captured".
/// </summary>
public sealed class IdempotencyRecord
{
    public string Key { get; private set; } = null!;

    /// <summary>
    /// A keyed hash (HMAC) of method + path + request body, to catch a key
    /// reused for a *different* request. Keyed for the same reason as the
    /// card fingerprint: the body contains a card number.
    /// </summary>
    public string RequestHash { get; private set; } = null!;

    /// <summary>null until the request finishes; it never stays null after commit.</summary>
    public int? ResponseStatus { get; private set; }
    public string? ResponseBody { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IdempotencyRecord(string key, string requestHash)
    {
        Key = key;
        RequestHash = requestHash;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void Complete(int status, string body)
    {
        ResponseStatus = status;
        ResponseBody = body;
    }

    private IdempotencyRecord() { }   // EF Core
}
