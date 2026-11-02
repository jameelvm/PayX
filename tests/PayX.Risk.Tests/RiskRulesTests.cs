using PayX.Contracts;
using PayX.Risk.Domain;

namespace PayX.Risk.Tests;

public class RiskRulesTests
{
    private static readonly RiskRulesSettings Settings = new()
    {
        AmountBands = new() { ["USD"] = new() { ElevatedMinor = 100_000, HighMinor = 500_000 } },
        HomeCountry = "US",
        HighRiskCountries = ["XR"],
        Velocity = new() { Window = TimeSpan.FromMinutes(10), ElevatedCount = 3, HighCount = 5 },
    };

    private static RiskScore Score(long amountMinor, string currency = "USD", string? country = "US", int recent = 1) =>
        RiskRules.Score(new RiskInput("pay_test", new Money(amountMinor, currency), country, recent), Settings);

    private static string[] Codes(RiskScore s) => s.Reasons.Select(r => r.Code).ToArray();

    [Fact]
    public void Small_domestic_payment_scores_zero_with_no_reasons()
    {
        var s = Score(5_000);
        Assert.Equal(0, s.Score);
        Assert.Empty(s.Reasons);
    }

    [Theory]
    [InlineData(99_999, 0)]                          // just under the band
    [InlineData(100_000, RiskRules.AmountElevated)]  // bands are inclusive
    [InlineData(499_999, RiskRules.AmountElevated)]
    [InlineData(500_000, RiskRules.AmountHigh)]      // high replaces elevated, never both
    public void Amount_bands(long amountMinor, int expected) =>
        Assert.Equal(expected, Score(amountMinor).Score);

    [Fact]
    public void Currency_without_bands_is_not_treated_as_safe()
    {
        // ¥5,000 is small, but no JPY bands are configured here: Risk can't tell.
        var s = Score(5_000, "JPY");
        Assert.Equal(["amount_unrated"], Codes(s));
        Assert.Equal(RiskRules.AmountUnrated, s.Score);
    }

    [Theory]
    [InlineData("US", 0, new string[0])]
    [InlineData("us", 0, new string[0])]                                 // case doesn't matter
    [InlineData("GB", RiskRules.CrossBorder, new[] { "cross_border" })]
    [InlineData("XR", RiskRules.HighRiskCountry, new[] { "card_country_high_risk" })]  // not also cross_border
    [InlineData(null, RiskRules.CardCountryUnknown, new[] { "card_country_unknown" })]
    public void Card_country(string? country, int expected, string[] codes)
    {
        var s = Score(5_000, country: country);
        Assert.Equal(expected, s.Score);
        Assert.Equal(codes, Codes(s));
    }

    [Fact]
    public void Rules_add_up_and_every_one_that_fired_is_a_reason()
    {
        var s = Score(150_000, country: "GB");
        Assert.Equal(RiskRules.AmountElevated + RiskRules.CrossBorder, s.Score);
        Assert.Equal(["amount_elevated", "cross_border"], Codes(s));
    }

    [Fact]
    public void Score_is_capped_at_100_but_keeps_every_reason()
    {
        var s = Score(900_000, country: "XR");   // 50 + 60
        Assert.Equal(RiskRules.MaxScore, s.Score);
        Assert.Equal(["amount_high", "card_country_high_risk"], Codes(s));
    }

    [Theory]
    [InlineData(1, 0)]                                // this payment alone
    [InlineData(2, 0)]
    [InlineData(3, RiskRules.VelocityElevated)]       // limits are inclusive
    [InlineData(4, RiskRules.VelocityElevated)]
    [InlineData(5, RiskRules.VelocityHigh)]           // high replaces elevated
    [InlineData(12, RiskRules.VelocityHigh)]
    public void Velocity(int recent, int expected) =>
        Assert.Equal(expected, Score(5_000, recent: recent).Score);
}
