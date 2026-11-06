using PayX.Payment.Services;

namespace PayX.Payment.Tests;

/// <summary>A Risk service that always answers the same; counts how often it was asked.</summary>
public sealed class FixedRisk(RiskDecision decision, int score = 0, params string[] reasons) : IRiskCheck
{
    public static FixedRisk Allow() => new(RiskDecision.Allow);

    public int Calls { get; private set; }

    public Task<RiskCheckResult> CheckAsync(Domain.Payment payment, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new RiskCheckResult(new RiskAssessment(decision, score, reasons)));
    }
}

/// <summary>A Risk service that never answers (down, or failing).</summary>
public sealed class FailingRisk(int status, string error) : IRiskCheck
{
    public Task<RiskCheckResult> CheckAsync(Domain.Payment payment, CancellationToken ct) =>
        Task.FromResult(new RiskCheckResult(null, status, error));
}
