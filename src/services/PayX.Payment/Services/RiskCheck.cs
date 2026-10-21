namespace PayX.Payment.Services;

public enum RiskDecision
{
    Allow,
    Block,   // Challenge (3-D Secure style) arrives with the real Risk service, 5.3
}

/// <summary>
/// The doc's "fraud detection + risk check" step, as Payment sees it: one
/// question, one answer, before any money is asked for.
/// </summary>
public interface IRiskCheck
{
    Task<RiskDecision> CheckAsync(Domain.Payment payment, CancellationToken ct);
}

/// <summary>
/// STAND-IN until Phase 5: allows everything, so the orchestration around it
/// can be built and tested now. Phase 5 replaces this registration with a
/// client for the Risk service (rules, velocity, timeout and fallback).
/// </summary>
public sealed class AllowAllRiskCheck : IRiskCheck
{
    public Task<RiskDecision> CheckAsync(Domain.Payment payment, CancellationToken ct) =>
        Task.FromResult(RiskDecision.Allow);
}
