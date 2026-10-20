using Amazon.DynamoDBv2;
using Amazon.KeyManagementService;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PayX.Vault.Health;

// Vault can do nothing useful without both: no KMS, no encryption or
// decryption; no table, nowhere to keep cards. Readiness only, like Postgres
// in 1.5: an AWS outage should take Vault out of rotation, not restart it.

public sealed class KmsReadinessCheck(IAmazonKeyManagementService kms, IConfiguration config) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            // Resolves the alias too: "key exists and is usable", not just "KMS answers".
            var key = await kms.DescribeKeyAsync(config["Vault:KmsKeyId"], ct);
            return key.KeyMetadata.Enabled == true
                ? HealthCheckResult.Healthy($"key {key.KeyMetadata.KeyId} enabled")
                : HealthCheckResult.Unhealthy($"key {key.KeyMetadata.KeyId} disabled");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}

public sealed class DynamoDbReadinessCheck(IAmazonDynamoDB dynamo, IConfiguration config) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var table = await dynamo.DescribeTableAsync(config["Vault:TableName"], ct);
            return table.Table.TableStatus == TableStatus.ACTIVE
                ? HealthCheckResult.Healthy($"table {table.Table.TableName} active")
                : HealthCheckResult.Unhealthy($"table {table.Table.TableName} {table.Table.TableStatus}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
