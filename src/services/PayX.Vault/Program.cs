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

// Typed client for the PSP, with retries (6.3): every call carries the
// caller's idempotency key, so repeating it can't authorize twice. Retried
// here, at the hop closest to the PSP, and nowhere above (Payment doesn't
// retry Vault): retries at every layer multiply (3 × 3 × 3 = 27 attempts).
builder.Services.AddHttpClient<PspClient>(http =>
        http.BaseAddress = new Uri(builder.Configuration["Psp:BaseUrl"]
            ?? throw new InvalidOperationException("Missing configuration: Psp:BaseUrl")))
    .AddKeyedRetries(builder.Configuration.GetSection("Psp:Resilience"));

var app = builder.Build();

app.MapPayXDefaults();

app.Run();
