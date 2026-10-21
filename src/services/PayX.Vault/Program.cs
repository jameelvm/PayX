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
builder.AddRedis();   // CVCs until their authorization completes (4.2a), authorization answers (4.7)

builder.Services.AddHealthChecks()
    .AddCheck<KmsReadinessCheck>("kms:card-vault", tags: [HealthEndpoints.ReadyTag], timeout: TimeSpan.FromSeconds(2))
    .AddCheck<DynamoDbReadinessCheck>("dynamodb:card-vault", tags: [HealthEndpoints.ReadyTag], timeout: TimeSpan.FromSeconds(2));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CardEncryptor>();
builder.Services.AddSingleton<CardRepository>();
builder.Services.AddSingleton<CvcStore>();
builder.Services.AddSingleton<AuthorizationAnswers>();
builder.Services.AddSingleton<CardVault>();
builder.Services.AddScoped<CardAuthorizer>();

// Typed client for the PSP. A plain timeout for now; retries, backoff and a
// circuit breaker (Polly) come in Phases 5-6.
builder.Services.AddHttpClient<PspClient>(http =>
{
    http.BaseAddress = new Uri(builder.Configuration["Psp:BaseUrl"]
        ?? throw new InvalidOperationException("Missing configuration: Psp:BaseUrl"));
    http.Timeout = builder.Configuration.GetValue("Psp:Timeout", TimeSpan.FromSeconds(10));
});

var app = builder.Build();

app.MapPayXDefaults();

app.Run();
