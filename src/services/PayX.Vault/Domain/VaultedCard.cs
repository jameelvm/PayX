using System.Security.Cryptography;

namespace PayX.Vault.Domain;

/// <summary>
/// One card in the vault: the encrypted number plus the few details that are
/// safe to show (PCI DSS allows the last 4 digits, brand and expiry in the
/// clear). This is everything stored; there is deliberately no CVV field.
/// <paramref name="Country"/>: the issuing country, from the BIN (5.4); null
/// when the BIN isn't in the table.
/// </summary>
public sealed record VaultedCard(
    string Token, EncryptedCard Encrypted, string Brand, string Last4, int ExpMonth, int ExpYear, DateTimeOffset CreatedAt,
    string? Country = null)
{
    // Random, not derived from the card number: a token reveals nothing and
    // can't be recomputed from a stolen card list. Contrast with the PSP's
    // fingerprint (3.2), an HMAC of the number, built to *match* cards.
    public static string NewToken() => "tok_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

public static class CardNumber
{
    public static bool IsValid(string number) =>
        number.Length is >= 12 and <= 19 && number.All(char.IsAsciiDigit) && PassesLuhn(number);

    // Only for display ("Visa •••• 4242"). Whether the network accepts the
    // card is the PSP's call, not the vault's.
    public static string BrandOf(string number) => number switch
    {
        ['4', ..] => "visa",
        ['5', >= '1' and <= '5', ..] => "mastercard",
        _ => "unknown",
    };

    // Issuer BIN (bank identification number) → issuing country, ISO 3166
    // alpha-2. Real tables come licensed from the card networks or a BIN
    // data provider and hold hundreds of thousands of ranges; this one holds
    // the simulator's test cards. XR is a user-assigned ISO code, so no real
    // country is labelled high-risk (Risk's settings, 5.1).
    private static readonly Dictionary<string, string> BinCountries = new()
    {
        ["424242"] = "US",       // 4242 4242 4242 4242
        ["400000"] = "US",       // the decline test cards, 4000 0000 0000 xxxx
        ["555555"] = "US",       // 5555 5555 5555 4444
        ["40000082"] = "GB",     // 4000 0082 6000 0000
        ["40000099"] = "XR",     // 4000 0099 0000 0004
    };

    /// <summary>
    /// The issuing country, for Risk (5.1's country rule), from the card's
    /// leading digits; null if unknown. Longest prefix first: BINs have been
    /// 8 digits since 2022 (ISO/IEC 7812), and the GB and XR cards above
    /// share their first 6 digits with the US range.
    /// </summary>
    public static string? CountryOf(string number)
    {
        foreach (var length in (int[])[8, 6])
            if (number.Length > length && BinCountries.TryGetValue(number[..length], out var country))
                return country;
        return null;
    }

    private static bool PassesLuhn(string number)
    {
        var sum = 0;
        for (var i = 0; i < number.Length; i++)
        {
            var digit = number[^(i + 1)] - '0';
            if (i % 2 == 1 && (digit *= 2) > 9) digit -= 9;
            sum += digit;
        }
        return sum % 10 == 0;
    }
}
