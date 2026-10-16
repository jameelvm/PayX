namespace PayX.PspSimulator.Domain;

public enum CardBrand { Visa, Mastercard }

/// <summary>
/// The card network's part of an authorization (Visa, Mastercard: doc ch.1,
/// "validates the credit or debit card information"). It doesn't know
/// balances; it checks the number is well formed and works out, from the
/// first digits, which network (and so which issuer) the request goes to.
/// </summary>
public static class CardNetwork
{
    /// <summary>
    /// Luhn checksum: the last digit of every card number is computed from
    /// the others, so a mistyped digit or two swapped neighbours fails
    /// before any bank is contacted. It catches typos, not fraud: anyone can
    /// generate a number that passes.
    /// </summary>
    public static bool PassesLuhn(string number)
    {
        if (number.Length is < 12 or > 19 || !number.All(char.IsAsciiDigit)) return false;

        var sum = 0;
        for (var i = 0; i < number.Length; i++)
        {
            var digit = number[number.Length - 1 - i] - '0';
            if (i % 2 == 1)                     // every second digit from the right…
            {
                digit *= 2;                     // …is doubled,
                if (digit > 9) digit -= 9;      // and 2-digit results are summed (16 → 1+6 = 7)
            }
            sum += digit;
        }
        return sum % 10 == 0;
    }

    /// <summary>
    /// The brand from the BIN (bank identification number, the leading
    /// digits). Real networks route on the first 6–8 digits to reach the
    /// exact issuing bank; the first one or two are enough to tell brands.
    /// </summary>
    public static CardBrand? BrandOf(string number) => number switch
    {
        ['4', ..] => CardBrand.Visa,
        ['5', >= '1' and <= '5', ..] => CardBrand.Mastercard,   // 51–55
        _ => null,                                              // a network this simulator doesn't carry
    };
}
