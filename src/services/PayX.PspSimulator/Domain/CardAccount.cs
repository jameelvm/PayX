namespace PayX.PspSimulator.Domain;

/// <summary>
/// The issuing bank's account behind one card: its credit limit. What is
/// *available* is not stored here; it is computed from the holds and
/// charges against the account (see <c>Authorizer</c>), so it can never
/// drift out of step with them.
/// </summary>
public sealed class CardAccount
{
    /// <summary>
    /// Identifies the card without storing its number: a keyed hash (HMAC) of
    /// it. A plain SHA-256 would not do: card numbers have so few unknown
    /// digits (the BIN is public, the last digit is a checksum) that every
    /// possible number can be hashed and looked up in hours.
    /// </summary>
    public string Fingerprint { get; private set; } = null!;

    public long LimitMinor { get; private set; }

    public CardAccount(string fingerprint, long limitMinor)
    {
        Fingerprint = fingerprint;
        LimitMinor = limitMinor;
    }

    private CardAccount() { }   // EF Core
}
