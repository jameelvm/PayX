namespace PayX.Payment.Services;

public enum RiskDecision
{
    Allow,
    Challenge,
    Block,
}

/// <summary>Risk's answer: what to do, and why (score and the rules that fired).</summary>
/// <param name="Score">Null when Risk didn't score it (the fallback, 5.7).</param>
public sealed record RiskAssessment(RiskDecision Decision, int? Score, IReadOnlyList<string> Reasons);

/// <param name="Assessment">Set when Risk answered.</param>
/// <param name="FailureStatus">Otherwise the HTTP status to report (502, 504).</param>
public sealed record RiskCheckResult(RiskAssessment? Assessment, int FailureStatus = 0, string? Failure = null);

/// <summary>
/// The doc's "fraud detection + risk check" step, as Payment sees it: one
/// question, one answer, before any money is asked for. Implemented by
/// <see cref="RiskClient"/> (5.5), wrapped in <see cref="FallbackRiskCheck"/>
/// (5.7); tests substitute their own.
/// </summary>
public interface IRiskCheck
{
    Task<RiskCheckResult> CheckAsync(Domain.Payment payment, CancellationToken ct);
}
