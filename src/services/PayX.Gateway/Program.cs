using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using PayX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();

// Every request's "Authorization: Bearer <jwt>" is checked here, at the edge,
// before YARP forwards anything. A bad token never reaches a service.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection("Jwt");

        // Identity's discovery document → jwks_uri → public key, fetched and
        // cached by the middleware; refetched when a token shows an unknown
        // kid. The Gateway never holds a key file of its own.
        options.MetadataAddress = jwt["MetadataAddress"]!;
        options.RequireHttpsMetadata = false;   // local only: plain http to Identity

        // Keep claim names as issued ("sub", "role"), instead of .NET's habit
        // of renaming them to long schema URLs.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt["Issuer"],          // iss must be payx-identity
            ValidAudience = jwt["Audience"],      // aud must be payx-api
            // Only the algorithm Identity actually uses. Without this, a
            // forged token could claim "alg": "none" (no signature at all) or
            // switch algorithms to trick the verifier.
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            // exp is checked by default; allow 30 s for clocks that disagree
            // (the library default is 5 minutes, a third of our token's life).
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "unique_name",
            RoleClaimType = "role",
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Secure by default: any endpoint or route without its own policy
    // requires a valid token. Forgetting to protect a new route locks it
    // instead of leaving it open. Public routes must say so explicitly
    // ("anonymous" in appsettings.json).
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    // Coarse, service-level rules only. Which role may call which endpoint
    // inside a service is that service's decision (Phase 4 onwards).
    options.AddPolicy("payments", p => p.RequireRole("Customer", "Merchant"));
});

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// Order matters: middleware runs in the order it is added. MapPayXDefaults
// adds the error-formatting middleware (ProblemDetails, Module 1.1), so it
// must come first, or the 401/403 written by authentication below would leave
// with an empty body instead of the one error shape every service uses.
app.MapPayXDefaults();

app.UseAuthentication();
app.UseAuthorization();

app.MapReverseProxy();

app.Run();
