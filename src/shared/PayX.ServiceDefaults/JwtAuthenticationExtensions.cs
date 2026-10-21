using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace PayX.ServiceDefaults;

/// <summary>
/// Validation of the access tokens Identity issues (Module 2.4), shared by the
/// Gateway and every service that reads who the caller is. Moved here from the
/// Gateway in Module 4.2c, unchanged, when Payment started reading <c>sub</c>:
/// a service doesn't trust the network, it checks the token itself too
/// (DESIGN.md decision 21, defense in depth).
/// </summary>
public static class JwtAuthenticationExtensions
{
    public static WebApplicationBuilder AddPayXJwtAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var jwt = builder.Configuration.GetSection("Jwt");

                // Identity's discovery document → jwks_uri → public key, fetched and
                // cached by the middleware; refetched when a token shows an unknown
                // kid. No service holds a key file of its own.
                options.MetadataAddress = jwt["MetadataAddress"]
                    ?? throw new InvalidOperationException("Missing configuration: Jwt:MetadataAddress");
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

        return builder;
    }
}
