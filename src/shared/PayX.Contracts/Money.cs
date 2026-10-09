using System.Globalization;

namespace PayX.Contracts;

/// <summary>
/// An amount of money: a whole number of the currency's minor unit (cents for
/// USD, yen for JPY) plus its ISO-4217 currency code. Every amount that crosses
/// a service boundary in PayX is one of these (DESIGN decision 17).
///
/// Why not <c>double</c>: binary floating point can't represent 0.10 exactly,
/// so 0.1 + 0.2 != 0.3 and errors accumulate across millions of postings —
/// a ledger that drifts by fractions of a cent never reconciles.
/// Why not a bare <c>decimal</c>: it carries no currency, and JSON/JavaScript
/// parse numbers as doubles anyway. An integer count of minor units survives
/// every hop exactly (up to the JavaScript-safe limit below), and the
/// currency travels with it.
///
/// A sealed record *class*, not a record struct: a struct always has a hidden
/// parameterless constructor, so <c>default(Money)</c> and System.Text.Json
/// could create one with a null currency without running the validation
/// below (the JSON round-trip test caught exactly that). A class can only be
/// built through its constructor, and a missing amount is plain
/// <c>null</c>, which nullable reference types flag at compile time.
/// </summary>
public sealed record class Money
{
    /// <summary>
    /// 2^53 - 1: the largest integer a JavaScript <c>Number</c> holds exactly.
    /// Amounts travel as JSON numbers to a browser, and JSON numbers are
    /// parsed as doubles, so a larger amount would silently change on the way.
    /// Still ~$90 trillion in cents, so no real payment comes near it; the cap
    /// makes the limit fail loudly instead of corrupting a value.
    /// </summary>
    public const long MaxAmountMinor = 9_007_199_254_740_991;

    // Minor-unit exponent per currency: how many decimal places the currency
    // has. Not always 2 — "cents" is a USD assumption. Unknown currencies are
    // rejected rather than guessed: a JPY amount treated as having cents would
    // be off by 100x. A real system loads the full ISO-4217 table.
    private static readonly Dictionary<string, int> Exponents = new()
    {
        ["USD"] = 2, ["EUR"] = 2, ["GBP"] = 2, ["INR"] = 2,
        ["JPY"] = 0,   // no minor unit: ¥500 is 500
        ["KWD"] = 3,   // Kuwaiti dinar: 1 dinar = 1000 fils
    };

    public long AmountMinor { get; }
    public string Currency { get; }

    public Money(long amountMinor, string currency)
    {
        if (currency is null || !Exponents.ContainsKey(currency))
            throw new ArgumentException($"Unsupported currency '{currency}'", nameof(currency));
        if (amountMinor is > MaxAmountMinor or < -MaxAmountMinor)
            throw new ArgumentOutOfRangeException(nameof(amountMinor), amountMinor,
                $"Beyond ±{MaxAmountMinor}, the largest amount JSON clients can represent exactly");
        AmountMinor = amountMinor;
        Currency = currency;
    }

    public static Money Zero(string currency) => new(0, currency);

    /// <summary>
    /// From a human amount like 12.34. Refuses amounts finer than the currency
    /// allows (12.345 USD) instead of rounding them: silently rounding money
    /// is how fractions of cents go missing.
    /// </summary>
    public static Money FromMajor(decimal amount, string currency)
    {
        var scaled = amount * Factor(currency);
        if (scaled != decimal.Truncate(scaled))
            throw new ArgumentException($"{amount} has more decimal places than {currency} allows", nameof(amount));
        return new Money(checked((long)scaled), currency);
    }

    public decimal ToMajor() => (decimal)AmountMinor / Factor(Currency);

    // Arithmetic only between the same currency. A result past the safe limit
    // throws (constructor check); `checked` additionally guarantees the long
    // arithmetic itself can never wrap to a large negative balance.
    public static Money operator +(Money a, Money b) => new(checked(a.AmountMinor + SameCurrency(a, b).AmountMinor), a.Currency);
    public static Money operator -(Money a, Money b) => new(checked(a.AmountMinor - SameCurrency(a, b).AmountMinor), a.Currency);

    // Negative amounts are legal: double-entry postings are signed (Phase 7).
    public static Money operator -(Money a) => new(checked(-a.AmountMinor), a.Currency);

    /// <summary>
    /// Splits into <paramref name="parts"/> amounts that add back up to exactly
    /// this one — $10.00 / 3 = 3.34 + 3.33 + 3.33, never 3.33 × 3 = 9.99.
    /// Needed when one order pays several sellers (doc ch.2, Wallet).
    /// </summary>
    public Money[] Allocate(int parts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 1);
        var share = AmountMinor / parts;
        var remainder = AmountMinor % parts;     // same sign as AmountMinor
        var result = new Money[parts];
        for (var i = 0; i < parts; i++)
        {
            // The leftover minor units go one each to the first parts.
            var extra = i < Math.Abs(remainder) ? Math.Sign(remainder) : 0;
            result[i] = new Money(share + extra, Currency);
        }
        return result;
    }

    public override string ToString() =>
        $"{Currency} {ToMajor().ToString("F" + Exponents[Currency], CultureInfo.InvariantCulture)}";

    private static decimal Factor(string currency) =>
        Exponents.TryGetValue(currency, out var exponent)
            ? (decimal)Math.Pow(10, exponent)
            : throw new ArgumentException($"Unsupported currency '{currency}'", nameof(currency));

    private static Money SameCurrency(Money a, Money b) =>
        a.Currency == b.Currency
            ? b
            : throw new InvalidOperationException($"Cannot combine {a.Currency} and {b.Currency}");
}
