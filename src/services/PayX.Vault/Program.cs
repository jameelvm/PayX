using Amazon.KeyManagementService;
using PayX.ServiceDefaults;
using PayX.Vault.Services;

// The only service that ever sees a full card number (CLAUDE.md: "raw PANs
// never leave Vault"). Module 3.6 is just the encryption core; the
// tokenize/detokenize API and DynamoDB storage arrive in 3.7.
var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();
builder.AddAwsClient<IAmazonKeyManagementService>();

builder.Services.AddSingleton<CardEncryptor>();

var app = builder.Build();

app.MapPayXDefaults();

app.Run();
