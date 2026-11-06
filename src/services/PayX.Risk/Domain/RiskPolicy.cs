namespace PayX.Risk.Domain;

/// <summary>What Payment should do with this payment.</summary>
public enum RiskDecision
{
    /// <summary>Go ahead and ask the bank.</summary>
    Allow,

    /// <summary>Ask the customer to prove it's them first (3-D Secure style step-up).</summary>
    Challenge,

    /// <summary>Refuse; the bank is never asked.</summary>
    Block,
}

/// <summary>
/// The business's risk appetite, from configuration (<c>Risk:Decision</c>).
/// Scores at or above a threshold get that decision.
/// </summary>
public sealed class DecisionThresholds
{
    public int ChallengeAt { get; init; } = 40;
    public int BlockAt { get; init; } = 80;

    public bool IsValid => ChallengeAt is >= 1 and <= RiskRules.MaxScore
        && BlockAt is >= 1 and <= RiskRules.MaxScore
        && ChallengeAt <= BlockAt;
}

/// <summary>
/// Doc ch.2: the risk check *decides*; fraud detection only *detects*
/// (5.1–5.2's score). Kept apart because they change for different reasons
/// and are owned by different people:
/// <list type="bullet">
/// <item>the score means "how suspicious": fraud analysts tune its rules as
/// fraud patterns change;</item>
/// <item>the thresholds mean "how much risk we accept": the business sets
/// them, trading lost sales (too strict) against fraud losses and
/// chargebacks (too lax), and moves them for a sale day or an attack
/// without touching a single rule.</item>
/// </list>
/// Three outcomes, not two: between "fine" and "fraud" is a band where
/// blocking loses good customers and allowing lets fraud through. A
/// challenge asks the customer to prove it's them, which a fraudster with
/// only the card details can't.
/// </summary>
public static class RiskPolicy
{
    public static RiskDecision Decide(int score, DecisionThresholds thresholds) =>
        score >= thresholds.BlockAt ? RiskDecision.Block
        : score >= thresholds.ChallengeAt ? RiskDecision.Challenge
        : RiskDecision.Allow;
}
