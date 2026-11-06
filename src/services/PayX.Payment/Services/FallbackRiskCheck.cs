namespace PayX.Payment.Services;

/// <summary>Per currency, in its minor units: below this, a payment may skip an unavailable Risk.</summary>
public sealed class RiskFallbackSettings
{
    public Dictionary<string, long> AllowBelowMinor { get; init; } = new();
}

/// <summary>
/// Doc ch.2, "fallbacks" (DESIGN decision 12): when Risk can't answer
/// (down, timing out, its circuit open), a small payment goes ahead
/// unchecked and a large one waits.
///
/// The trade-off is the whole point:
/// <list type="bullet">
/// <item>fail open (allow everything): a Risk outage becomes a fraud
/// window, and fraudsters notice outages;</item>
/// <item>fail closed (refuse everything): a Risk outage becomes a checkout
/// outage, and every honest customer pays for it;</item>
/// <item>threshold: small payments, where fraud costs little, keep flowing;
/// large ones, where it costs a lot, wait for Risk. The threshold is the
/// business's risk appetite, in configuration.</item>
/// </list>
/// Above the threshold, or in a currency with none, the failure stands: the
/// payment stays Created and the customer may retry (a retryable "not now",
/// not a final RiskBlocked: Risk never judged the payment).
/// </summary>
public sealed class FallbackRiskCheck(RiskClient risk, IConfiguration config) : IRiskCheck
{
    public const string FallbackReason = "risk_unavailable_fallback";

    public async Task<RiskCheckResult> CheckAsync(Domain.Payment payment, CancellationToken ct)
    {
        var result = await risk.CheckAsync(payment, ct);
        if (result.Assessment is not null)
            return result;

        var settings = config.GetSection("Risk:Fallback").Get<RiskFallbackSettings>() ?? new();
        if (settings.AllowBelowMinor.TryGetValue(payment.Amount.Currency, out var limit)
            && payment.Amount.AmountMinor < limit)
        {
            // No score (Risk never scored it); the reason marks it for a
            // later look, e.g. re-scoring once Risk is back.
            return new RiskCheckResult(new RiskAssessment(RiskDecision.Allow, null, [FallbackReason]));
        }

        return result;
    }
}
