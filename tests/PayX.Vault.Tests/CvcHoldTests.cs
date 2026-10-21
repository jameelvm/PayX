using System.Text.Json;

namespace PayX.Vault.Tests;

// The CVC's whole life (4.2a, changed in 4.7): held encrypted, kept until
// its authorization completes, then forgotten; gone after its TTL regardless.
public sealed class CvcHoldTests
{
    private const string Card = "4242424242424242";

    [Fact]
    public async Task The_cvc_is_held_until_it_is_forgotten()
    {
        var vault = TestVault.Create();
        var card = await vault.TokenizeAsync(Card, 12, 2030, cvc: "123", default);

        // Readable more than once: a retry after a timeout needs it again.
        Assert.Equal("123", await vault.GetCvcAsync(card.Token, default));
        Assert.Equal("123", await vault.GetCvcAsync(card.Token, default));

        await vault.ForgetCvcAsync(card.Token);

        Assert.Null(await vault.GetCvcAsync(card.Token, default));
    }

    [Fact]
    public async Task Redis_holds_it_encrypted_with_a_ttl()
    {
        var vault = TestVault.Create(cvcTtl: TimeSpan.FromMinutes(10));
        var card = await vault.TokenizeAsync(Card, 12, 2030, cvc: "987", default);

        var db = TestVault.Redis.GetDatabase();
        var stored = (string)(await db.StringGetAsync($"vault:cvc:{card.Token}"))!;
        var ttl = await db.KeyTimeToLiveAsync($"vault:cvc:{card.Token}");

        // Only the envelope: ciphertext (nonce + 3 digits + tag), wrapped key, key ARN.
        // (Not a "doesn't contain 987" check: base64 has digits and could match by chance.)
        using var json = JsonDocument.Parse(stored);
        Assert.Equal(["Ciphertext", "EncryptedDataKey", "KmsKeyArn"],
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(12 + 3 + 16, json.RootElement.GetProperty("Ciphertext").GetBytesFromBase64().Length);
        Assert.InRange(ttl!.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task An_unused_cvc_expires()
    {
        var vault = TestVault.Create(cvcTtl: TimeSpan.FromSeconds(1));
        var card = await vault.TokenizeAsync(Card, 12, 2030, cvc: "123", default);

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.Null(await vault.GetCvcAsync(card.Token, default));
    }

    [Fact]
    public async Task No_cvc_given_means_none_held()
    {
        var vault = TestVault.Create();
        var card = await vault.TokenizeAsync(Card, 12, 2030, cvc: null, default);

        Assert.Null(await vault.GetCvcAsync(card.Token, default));
    }
}
