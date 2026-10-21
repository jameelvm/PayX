using Amazon.DynamoDBv2.Model;
using PayX.Vault.Services;

namespace PayX.Vault.Tests;

// Integration tests against LocalStack's KMS and DynamoDB (and Redis), like CardEncryptorTests.
public sealed class CardVaultTests
{
    private const string Card = "4242424242424242";

    private readonly CardVault _vault = TestVault.Create();

    [Fact]
    public async Task Tokenize_then_detokenize_returns_the_card_number()
    {
        var card = await _vault.TokenizeAsync(Card, 12, 2030, cvc: null, default);

        Assert.Matches("^tok_[0-9a-f]{32}$", card.Token);
        Assert.Equal(("visa", "4242"), (card.Brand, card.Last4));
        Assert.Equal(Card, await _vault.DetokenizeAsync(card.Token, default));
    }

    [Fact]
    public async Task Stored_item_has_no_card_number_and_no_cvc()
    {
        // Tokenized *with* a CVC: it still must not reach the table.
        var card = await _vault.TokenizeAsync(Card, 12, 2030, cvc: "123", default);

        var item = (await TestVault.Dynamo.GetItemAsync("payx-card-vault",
            new Dictionary<string, AttributeValue> { ["token"] = new() { S = card.Token } })).Item;

        Assert.Equal(
            ["brand", "ciphertext", "created_at", "encrypted_data_key", "exp_month", "exp_year", "kms_key_arn", "last4", "token"],
            item.Keys.Order());
        Assert.DoesNotContain(item.Values, v => v.S?.Contains(Card) == true);
    }

    [Fact]
    public async Task Same_card_tokenized_twice_gets_two_unrelated_tokens()
    {
        var first = await _vault.TokenizeAsync(Card, 12, 2030, cvc: null, default);
        var second = await _vault.TokenizeAsync(Card, 12, 2030, cvc: null, default);

        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public async Task Unknown_token_finds_nothing()
    {
        Assert.Null(await _vault.FindAsync("tok_does_not_exist", default));
        Assert.Null(await _vault.DetokenizeAsync("tok_does_not_exist", default));
    }
}
