using PayX.Risk.Domain;

namespace PayX.Risk.Tests;

public class RiskPolicyTests
{
    private static readonly DecisionThresholds Thresholds = new() { ChallengeAt = 40, BlockAt = 80 };

    [Theory]
    [InlineData(0, RiskDecision.Allow)]
    [InlineData(39, RiskDecision.Allow)]
    [InlineData(40, RiskDecision.Challenge)]   // thresholds are inclusive
    [InlineData(79, RiskDecision.Challenge)]
    [InlineData(80, RiskDecision.Block)]
    [InlineData(100, RiskDecision.Block)]
    public void Score_to_decision(int score, RiskDecision expected) =>
        Assert.Equal(expected, RiskPolicy.Decide(score, Thresholds));

    [Fact]
    public void Equal_thresholds_leave_no_challenge_band()
    {
        // A business that never wants to step customers up: allowed.
        var noChallenge = new DecisionThresholds { ChallengeAt = 60, BlockAt = 60 };
        Assert.Equal(RiskDecision.Allow, RiskPolicy.Decide(59, noChallenge));
        Assert.Equal(RiskDecision.Block, RiskPolicy.Decide(60, noChallenge));
    }

    [Theory]
    [InlineData(40, 80, true)]
    [InlineData(60, 60, true)]
    [InlineData(80, 40, false)]   // challenge above block: Challenge could never happen
    [InlineData(0, 80, false)]    // every payment challenged, even a score of 0
    [InlineData(40, 101, false)]  // Block could never happen
    public void Thresholds_must_be_ordered_and_within_the_scale(int challengeAt, int blockAt, bool valid) =>
        Assert.Equal(valid, new DecisionThresholds { ChallengeAt = challengeAt, BlockAt = blockAt }.IsValid);
}
