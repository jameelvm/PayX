using PayX.Risk.Domain;
using PayX.Risk.Repositories;
using PayX.ServiceDefaults;

// Doc ch.2: "fraud detection" and "risk check", as one service (DESIGN
// decision 11): they share every signal and own no separate data, so a
// second service would add a network hop with no ownership boundary.
// 5.1: static rules. 5.2: velocity in a Redis sliding window. 5.3: the
// score becomes a decision.
var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();
builder.AddRedis();   // risk:velocity:* (5.2)
builder.Services.Configure<RiskRulesSettings>(builder.Configuration.GetSection("Risk:Rules"));
// Thresholds that contradict each other (challenge above block) would make
// one decision unreachable: refuse to start with them.
builder.Services.AddOptions<DecisionThresholds>()
    .Bind(builder.Configuration.GetSection("Risk:Decision"))
    .Validate(t => t.IsValid, "Risk:Decision needs 1 <= ChallengeAt <= BlockAt <= 100")
    .ValidateOnStart();
builder.Services.AddSingleton<SlidingWindowCounter>();

var app = builder.Build();

app.MapPayXDefaults();

app.Run();
