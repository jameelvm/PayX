using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.KeyManagementService;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using PayX.Vault.Repositories;
using PayX.Vault.Services;

namespace PayX.Vault.Tests;

// Integration tests against LocalStack's KMS and DynamoDB, like CardEncryptorTests.
public sealed class CardVaultTests
{
    private const string Card = "4242424242424242";

    private readonly AmazonDynamoDBClient _dynamo = new(new BasicAWSCredentials("test", "test"),
        new AmazonDynamoDBConfig { ServiceURL = "http://localhost:4568", AuthenticationRegion = "us-east-1" });
    private readonly CardVault _vault;

    public CardVaultTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:KmsKeyId"] = "alias/payx-card-vault",
            ["Vault:TableName"] = "payx-card-vault",
        }).Build();
        var kms = new AmazonKeyManagementServiceClient(new BasicAWSCredentials("test", "test"),
            new AmazonKeyManagementServiceConfig { ServiceURL = "http://localhost:4568", AuthenticationRegion = "us-east-1" });
        _vault = new CardVault(new CardEncryptor(kms, config), new CardRepository(_dynamo, config), TimeProvider.System);
    }

    [Fact]
    public async Task Tokenize_then_detokenize_returns_the_card_number()
    {
        var card = await _vault.TokenizeAsync(Card, 12, 2030, default);

        Assert.Matches("^tok_[0-9a-f]{32}$", card.Token);
        Assert.Equal(("visa", "4242"), (card.Brand, card.Last4));
        Assert.Equal(Card, await _vault.DetokenizeAsync(card.Token, default));
    }

    [Fact]
    public async Task Stored_item_has_no_card_number_and_no_cvc()
    {
        var card = await _vault.TokenizeAsync(Card, 12, 2030, default);

        var item = (await _dynamo.GetItemAsync("payx-card-vault",
            new Dictionary<string, AttributeValue> { ["token"] = new() { S = card.Token } })).Item;

        Assert.Equal(
            ["brand", "ciphertext", "created_at", "encrypted_data_key", "exp_month", "exp_year", "kms_key_arn", "last4", "token"],
            item.Keys.Order());
        Assert.DoesNotContain(item.Values, v => v.S?.Contains(Card) == true);
    }

    [Fact]
    public async Task Same_card_tokenized_twice_gets_two_unrelated_tokens()
    {
        var first = await _vault.TokenizeAsync(Card, 12, 2030, default);
        var second = await _vault.TokenizeAsync(Card, 12, 2030, default);

        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public async Task Unknown_token_finds_nothing()
    {
        Assert.Null(await _vault.FindAsync("tok_does_not_exist", default));
        Assert.Null(await _vault.DetokenizeAsync("tok_does_not_exist", default));
    }
}
