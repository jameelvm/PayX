namespace PayX.PspSimulator.Domain;

/// <summary>
/// The issuing bank's part (doc ch.1, authorization steps 3–4: "validates the
/// card and checks for fraud and the available credit limit… if approved,
/// sends an authorization code"). The only party that knows the
/// cardholder's account, so the only one that can say yes or no.
///
/// Real behaviour is replaced by Stripe-style test cards: a specific number
/// always produces a specific outcome, so every path through PayX can be
/// triggered on demand.
/// </summary>
public static class IssuerBank
{
    // The decline reasons a real issuer returns, in the shape PSPs pass on.
    public const string GenericDecline = "card_declined";
    public const string InsufficientFunds = "insufficient_funds";
    public const string ExpiredCard = "expired_card";
    public const string IncorrectCvc = "incorrect_cvc";

    /// <summary>Credit limit for any card not on the list below.</summary>
    public const long DefaultLimitMinor = 500_000;   // 5,000.00 in a 2-decimal currency

    private static readonly Dictionary<string, string> TestCards = new()
    {
        ["4000000000000002"] = GenericDecline,       // "do not honour": the bank won't say why
        ["4000000000009995"] = InsufficientFunds,
        ["4000000000000069"] = ExpiredCard,          // the bank says expired, whatever the date says
        ["4000000000000127"] = IncorrectCvc,
    };

    /// <returns>null if approved, otherwise the decline reason.</returns>
    public static string? Decide(string number, int expMonth, int expYear, long amountMinor, DateOnly today)
    {
        if (TestCards.TryGetValue(number, out var scripted))
            return scripted;

        // A card is valid through the last day of its expiry month.
        var lastValidDay = new DateOnly(expYear, expMonth, 1).AddMonths(1).AddDays(-1);
        if (today > lastValidDay)
            return ExpiredCard;

        // The real check: is there enough available credit for this hold?
        return amountMinor > DefaultLimitMinor ? InsufficientFunds : null;
    }
}
