using Amazon.DynamoDBv2.Model;
using PayX.Vault.Domain;
using PayX.Vault.Services;

namespace PayX.Vault.Tests;

// The issuing country from the BIN (5.4): derived at tokenization, stored in
// the clear like brand and last 4, read back without touching KMS.
public sealed class CardCountryTests
{
    [Theory]
    [InlineData("4242424242424242", "US")]
    [InlineData("4000000000000002", "US")]   // the decline test cards
    [InlineData("5555555555554444", "US")]
    [InlineData("4000008260000000", "GB")]   // 8-digit BIN beats its 6-digit range (400000 = US)
    [InlineData("4000009900000004", "XR")]
    [InlineData("4111111111111111", null)]   // not in the table: unknown, not guessed
    public void Country_from_the_bin(string number, string? expected) =>
        Assert.Equal(expected, CardNumber.CountryOf(number));

    [Fact]
    public async Task Country_is_stored_and_read_back()
    {
        var vault = TestVault.Create();
        var card = await vault.TokenizeAsync("4000008260000000", 12, 2030, cvc: null, default);

        Assert.Equal("GB", card.Country);
        Assert.Equal("GB", (await vault.FindAsync(card.Token, default))!.Country);
    }

    [Fact]
    public async Task Unknown_country_is_left_out_of_the_item()
    {
        var vault = TestVault.Create();
        var card = await vault.TokenizeAsync("4111111111111111", 12, 2030, cvc: null, default);

        var item = (await TestVault.Dynamo.GetItemAsync("payx-card-vault",
            new Dictionary<string, AttributeValue> { ["token"] = new() { S = card.Token } })).Item;

        Assert.DoesNotContain("country", item.Keys);
        Assert.Null((await vault.FindAsync(card.Token, default))!.Country);
    }
}
