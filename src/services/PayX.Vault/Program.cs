using Amazon.DynamoDBv2;
using Amazon.KeyManagementService;
using PayX.ServiceDefaults;
using PayX.ServiceDefaults.Health;
using PayX.Vault.Health;
using PayX.Vault.Repositories;
using PayX.Vault.Services;

// The only service that ever sees a full card number (CLAUDE.md: "raw PANs
// never leave Vault"). No Gateway route yet: only PayX's own services call it
// (checkout reaches it through Payment from Phase 4).
var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();
builder.AddAwsClient<IAmazonKeyManagementService>();
builder.AddAwsClient<IAmazonDynamoDB>();

builder.Services.AddHealthChecks()
    .AddCheck<KmsReadinessCheck>("kms:card-vault", tags: [HealthEndpoints.ReadyTag], timeout: TimeSpan.FromSeconds(2))
    .AddCheck<DynamoDbReadinessCheck>("dynamodb:card-vault", tags: [HealthEndpoints.ReadyTag], timeout: TimeSpan.FromSeconds(2));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CardEncryptor>();
builder.Services.AddSingleton<CardRepository>();
builder.Services.AddSingleton<CardVault>();

var app = builder.Build();

app.MapPayXDefaults();

app.Run();
