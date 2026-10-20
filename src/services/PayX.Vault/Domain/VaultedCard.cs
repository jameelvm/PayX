using System.Security.Cryptography;

namespace PayX.Vault.Domain;

/// <summary>
/// One card in the vault: the encrypted number plus the few details that are
/// safe to show (PCI DSS allows the last 4 digits, brand and expiry in the
/// clear). This is everything stored; there is deliberately no CVV field.
/// </summary>
public sealed record VaultedCard(
    string Token, EncryptedCard Encrypted, string Brand, string Last4, int ExpMonth, int ExpYear, DateTimeOffset CreatedAt)
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
