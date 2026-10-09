using System.Text.Json;
using PayX.Contracts;

namespace PayX.Contracts.Tests;

public class MoneyTests
{
    [Fact]
    public void Double_cannot_represent_cents_exactly_which_is_why_Money_exists()
    {
        // The problem, demonstrated: ten 10-cent payments as doubles.
        double total = 0;
        for (var i = 0; i < 10; i++) total += 0.10;
        Assert.NotEqual(1.00, total);              // 0.9999999999999999

        // The same ten payments as Money: exact.
        var money = Money.Zero("USD");
        for (var i = 0; i < 10; i++) money += new Money(10, "USD");
        Assert.Equal(new Money(100, "USD"), money);
    }

    [Fact]
    public void FromMajor_converts_using_the_currencys_own_decimal_places()
    {
        Assert.Equal(1234, Money.FromMajor(12.34m, "USD").AmountMinor);
        Assert.Equal(500, Money.FromMajor(500m, "JPY").AmountMinor);       // no minor unit
        Assert.Equal(1500, Money.FromMajor(1.5m, "KWD").AmountMinor);      // 1.500 dinar
    }

    [Theory]
    [InlineData(12.345, "USD")]   // a tenth of a cent
    [InlineData(500.5, "JPY")]    // half a yen
    public void FromMajor_refuses_to_round_silently(decimal amount, string currency) =>
        Assert.Throws<ArgumentException>(() => Money.FromMajor(amount, currency));

    [Fact]
    public void Unknown_currency_is_rejected_not_guessed() =>
        Assert.Throws<ArgumentException>(() => new Money(100, "XYZ"));

    [Fact]
    public void Adding_different_currencies_throws()
    {
        var usd = new Money(100, "USD");
        var eur = new Money(100, "EUR");
        Assert.Throws<InvalidOperationException>(() => usd + eur);
    }

    [Fact]
    public void Amounts_beyond_the_JavaScript_safe_limit_are_rejected()
    {
        // Allowed: exactly 2^53 - 1, in either direction.
        Assert.Equal(Money.MaxAmountMinor, new Money(Money.MaxAmountMinor, "USD").AmountMinor);
        Assert.Equal(-Money.MaxAmountMinor, new Money(-Money.MaxAmountMinor, "USD").AmountMinor);

        // One more, or a long that a browser would silently round: rejected.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(Money.MaxAmountMinor + 1, "USD"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(long.MaxValue, "USD"));
    }

    [Fact]
    public void Arithmetic_that_crosses_the_limit_throws_instead_of_wrapping()
    {
        var max = new Money(Money.MaxAmountMinor, "USD");
        Assert.Throws<ArgumentOutOfRangeException>(() => max + new Money(1, "USD"));
    }

    [Fact]
    public void The_limit_is_where_JavaScript_starts_losing_integers()
    {
        // Why the cap is 2^53 - 1: past it, a double (what JSON.parse produces)
        // can no longer tell neighbouring integers apart.
        Assert.Equal((double)Money.MaxAmountMinor + 1, (double)Money.MaxAmountMinor + 2);
        Assert.NotEqual((double)Money.MaxAmountMinor - 1, (double)Money.MaxAmountMinor);
    }

    [Theory]
    [InlineData(1000, 3, new long[] { 334, 333, 333 })]   // $10.00 → 3.34 + 3.33 + 3.33
    [InlineData(100, 4, new long[] { 25, 25, 25, 25 })]
    [InlineData(-1000, 3, new long[] { -334, -333, -333 })] // a refund split the same way
    public void Allocate_never_loses_or_invents_a_minor_unit(long total, int parts, long[] expected)
    {
        var shares = new Money(total, "USD").Allocate(parts);

        Assert.Equal(expected, shares.Select(s => s.AmountMinor));
        Assert.Equal(total, shares.Sum(s => s.AmountMinor));
    }

    [Fact]
    public void ToString_uses_the_currencys_decimal_places()
    {
        Assert.Equal("USD 12.34", new Money(1234, "USD").ToString());
        Assert.Equal("JPY 500", new Money(500, "JPY").ToString());
        Assert.Equal("KWD 1.500", new Money(1500, "KWD").ToString());
    }

    [Fact]
    public void Round_trips_through_JSON_as_an_integer_plus_currency()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);   // what ASP.NET uses
        var json = JsonSerializer.Serialize(new Money(1234, "USD"), web);

        Assert.Equal("""{"amountMinor":1234,"currency":"USD"}""", json);
        Assert.Equal(new Money(1234, "USD"), JsonSerializer.Deserialize<Money>(json, web));
    }

    [Fact]
    public void Deserializing_runs_the_constructor_so_bad_input_is_rejected()
    {
        // A request body claiming an unsupported currency must fail at the
        // boundary, not become a Money that blows up three services later.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<Money>("""{"amountMinor":100,"currency":"XYZ"}""", web));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JsonSerializer.Deserialize<Money>("""{"amountMinor":9007199254740992,"currency":"USD"}""", web));
    }
}
