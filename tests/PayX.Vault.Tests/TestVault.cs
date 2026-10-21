using Amazon.DynamoDBv2;
using Amazon.KeyManagementService;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using PayX.Vault.Repositories;
using PayX.Vault.Services;
using StackExchange.Redis;

namespace PayX.Vault.Tests;

// The Vault wired to the local containers: LocalStack (KMS, DynamoDB) on
// 4568 and Redis on 6381. docker compose up -d localstack redis first.
internal static class TestVault
{
    public static readonly AmazonDynamoDBClient Dynamo = new(new BasicAWSCredentials("test", "test"),
        new AmazonDynamoDBConfig { ServiceURL = "http://localhost:4568", AuthenticationRegion = "us-east-1" });

    public static readonly IConnectionMultiplexer Redis = ConnectionMultiplexer.Connect("localhost:6381");

    private static readonly AmazonKeyManagementServiceClient Kms = new(new BasicAWSCredentials("test", "test"),
        new AmazonKeyManagementServiceConfig { ServiceURL = "http://localhost:4568", AuthenticationRegion = "us-east-1" });

    public static CardVault Create(TimeSpan? cvcTtl = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:KmsKeyId"] = "alias/payx-card-vault",
            ["Vault:TableName"] = "payx-card-vault",
            ["Vault:CvcTtl"] = (cvcTtl ?? TimeSpan.FromMinutes(10)).ToString(),
        }).Build();
        var encryptor = new CardEncryptor(Kms, config);
        return new CardVault(encryptor, new CardRepository(Dynamo, config), new CvcStore(Redis, encryptor, config),
            TimeProvider.System);
    }
}
