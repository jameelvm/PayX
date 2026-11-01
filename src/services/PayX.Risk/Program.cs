using PayX.Risk.Domain;
using PayX.ServiceDefaults;

// Doc ch.2: "fraud detection" and "risk check", as one service (DESIGN
// decision 11): they share every signal and own no separate data, so a
// second service would add a network hop with no ownership boundary.
// 5.1: static rules only. Redis velocity counters arrive in 5.2.
var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();
builder.Services.Configure<RiskRulesSettings>(builder.Configuration.GetSection("Risk:Rules"));

var app = builder.Build();

app.MapPayXDefaults();

app.Run();
