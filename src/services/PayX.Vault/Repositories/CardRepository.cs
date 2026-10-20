using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using PayX.Vault.Domain;

namespace PayX.Vault.Repositories;

/// <summary>
/// The <c>payx-card-vault</c> DynamoDB table: one item per token. A key-value
/// lookup by token is the only access pattern, which is what DynamoDB is for.
/// </summary>
public sealed class CardRepository(IAmazonDynamoDB dynamo, IConfiguration config)
{
    private readonly string _table = config["Vault:TableName"]
        ?? throw new InvalidOperationException("Missing configuration: Vault:TableName");

    public async Task AddAsync(VaultedCard card, CancellationToken ct) =>
        await dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _table,
            Item = new()
            {
                ["token"] = new() { S = card.Token },
                ["ciphertext"] = new() { B = new MemoryStream(card.Encrypted.Ciphertext) },
                ["encrypted_data_key"] = new() { B = new MemoryStream(card.Encrypted.EncryptedDataKey) },
                ["kms_key_arn"] = new() { S = card.Encrypted.KmsKeyArn },
                ["brand"] = new() { S = card.Brand },
                ["last4"] = new() { S = card.Last4 },
                ["exp_month"] = new() { N = card.ExpMonth.ToString(CultureInfo.InvariantCulture) },
                ["exp_year"] = new() { N = card.ExpYear.ToString(CultureInfo.InvariantCulture) },
                ["created_at"] = new() { S = card.CreatedAt.ToString("O") },
            },
            // A plain PutItem silently overwrites. 128 random bits won't
            // collide in practice, but if they ever did, overwriting would
            // swap one customer's card for another's: fail instead.
            ConditionExpression = "attribute_not_exists(#t)",
            ExpressionAttributeNames = new() { ["#t"] = "token" },
        }, ct);

    public async Task<VaultedCard?> FindAsync(string token, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = _table,
            Key = new() { ["token"] = new() { S = token } },
            ConsistentRead = true,   // a card tokenized a moment ago must be found
        }, ct);

        if (response.Item is not { Count: > 0 } item)
            return null;

        return new VaultedCard(
            item["token"].S,
            new EncryptedCard(item["ciphertext"].B.ToArray(), item["encrypted_data_key"].B.ToArray(), item["kms_key_arn"].S),
            item["brand"].S,
            item["last4"].S,
            int.Parse(item["exp_month"].N, CultureInfo.InvariantCulture),
            int.Parse(item["exp_year"].N, CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(item["created_at"].S, CultureInfo.InvariantCulture));
    }
}
