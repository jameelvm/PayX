using PayX.Contracts;

namespace PayX.Risk.Domain;

/// <summary>
/// What Risk knows about a payment. Card data arrives as metadata only (never
/// a PAN). <paramref name="RecentPayments"/> is the customer's payments in the
/// velocity window, this one included, counted before scoring (5.2), so the
/// rules stay a pure function of their input.
/// </summary>
public sealed record RiskInput(string PaymentId, Money Amount, string? CardCountry, int RecentPayments);

/// <summary>One rule that fired, and how much it added. The reasons travel with the score.</summary>
public sealed record RiskReason(string Code, int Points);

public sealed record RiskScore(int Score, IReadOnlyList<RiskReason> Reasons);

/// <summary>Amount bands for one currency, in its minor units.</summary>
public sealed class AmountBands
{
    public long ElevatedMinor { get; init; }
    public long HighMinor { get; init; }
}

/// <summary>
/// The policy half of the rules, from configuration (<c>Risk:Rules</c>):
/// thresholds and lists change more often than code, and a fraud team tunes
/// them without a deploy.
/// </summary>
public sealed class RiskRulesSettings
{
    /// <summary>Per currency: there is no single "large amount" across USD, JPY and KWD.</summary>
    public Dictionary<string, AmountBands> AmountBands { get; init; } = new();

    /// <summary>Where the platform's merchants are; a card from elsewhere is cross-border.</summary>
    public string HomeCountry { get; init; } = "US";

    /// <summary>ISO 3166 alpha-2 codes treated as high risk (sanctions, known fraud corridors).</summary>
    public List<string> HighRiskCountries { get; init; } = [];

    public VelocityLimits Velocity { get; init; } = new();
}

/// <summary>How many payments in how long is unusual (5.2).</summary>
public sealed class VelocityLimits
{
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);
    public int ElevatedCount { get; init; } = 3;
    public int HighCount { get; init; } = 5;
}

/// <summary>
/// Doc ch.2, "fraud detection + risk check", stage one (decision 11): static
/// rules that turn what we know about a payment into a score from 0 (no
/// concern) to 100. A pure function: no I/O, no clock, so every rule can be
/// tested on its own and replayed on yesterday's payments.
///
/// It only scores. Turning a score into allow / challenge / block is a
/// separate decision with its own thresholds (5.3): detection is "how
/// suspicious is this?", the decision is "what do we do about it?".
///
/// Every rule that fires is returned as a reason. A bare number can't be
/// explained to a customer, an analyst or a dispute; "amount_high +
/// cross_border" can.
/// </summary>
public static class RiskRules
{
    public const int MaxScore = 100;

    // Weights are code, thresholds are configuration: the weights define what
    // the score means, and 5.3's decision thresholds are set against them.
    public const int AmountHigh = 50;
    public const int AmountElevated = 20;
    public const int AmountUnrated = 20;       // no bands configured for the currency: unknown is not "safe"
    public const int HighRiskCountry = 60;
    public const int CrossBorder = 15;
    public const int CardCountryUnknown = 15;  // no country: can't rule anything out
    public const int VelocityHigh = 40;        // a burst: the pattern of testing stolen cards
    public const int VelocityElevated = 15;

    public static RiskScore Score(RiskInput input, RiskRulesSettings settings)
    {
        var reasons = new List<RiskReason>();
        AmountRule(input.Amount, settings, reasons);
        CountryRule(input.CardCountry, settings, reasons);
        VelocityRule(input.RecentPayments, settings.Velocity, reasons);

        // Capped: the score is a scale, not a sum to compare beyond "certainly suspicious".
        return new RiskScore(Math.Min(MaxScore, reasons.Sum(r => r.Points)), reasons);
    }

    private static void AmountRule(Money amount, RiskRulesSettings settings, List<RiskReason> reasons)
    {
        if (!settings.AmountBands.TryGetValue(amount.Currency, out var bands))
            reasons.Add(new("amount_unrated", AmountUnrated));
        else if (amount.AmountMinor >= bands.HighMinor)
            reasons.Add(new("amount_high", AmountHigh));
        else if (amount.AmountMinor >= bands.ElevatedMinor)
            reasons.Add(new("amount_elevated", AmountElevated));
    }

    private static void CountryRule(string? cardCountry, RiskRulesSettings settings, List<RiskReason> reasons)
    {
        if (string.IsNullOrEmpty(cardCountry))
            reasons.Add(new("card_country_unknown", CardCountryUnknown));
        else if (settings.HighRiskCountries.Contains(cardCountry, StringComparer.OrdinalIgnoreCase))
            reasons.Add(new("card_country_high_risk", HighRiskCountry));
        else if (!string.Equals(cardCountry, settings.HomeCountry, StringComparison.OrdinalIgnoreCase))
            reasons.Add(new("cross_border", CrossBorder));
    }

    private static void VelocityRule(int recentPayments, VelocityLimits limits, List<RiskReason> reasons)
    {
        if (recentPayments >= limits.HighCount)
            reasons.Add(new("velocity_high", VelocityHigh));
        else if (recentPayments >= limits.ElevatedCount)
            reasons.Add(new("velocity_elevated", VelocityElevated));
    }
}
