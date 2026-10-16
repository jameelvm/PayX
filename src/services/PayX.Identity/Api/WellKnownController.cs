using Microsoft.AspNetCore.Mvc;
using PayX.Identity.Services;

namespace PayX.Identity.Api;

/// <summary>
/// How a verifier (the Gateway, later any service) learns Identity's public
/// key without anyone copying key files around. These are the standard
/// OpenID Connect discovery paths, so off-the-shelf JWT middleware knows how
/// to read them: fetch the discovery document, follow <c>jwks_uri</c>, cache
/// the keys, and refetch when a token arrives with an unknown <c>kid</c>
/// (which is how key rotation reaches verifiers with no redeploy).
/// </summary>
[ApiController]
public sealed class WellKnownController(TokenIssuer tokens) : ControllerBase
{
    [HttpGet("/.well-known/openid-configuration")]
    public IActionResult Discovery() => Ok(new Dictionary<string, object>
    {
        ["issuer"] = tokens.Issuer,
        ["jwks_uri"] = $"{Request.Scheme}://{Request.Host}/.well-known/jwks.json",
        ["id_token_signing_alg_values_supported"] = new[] { "ES256" },
    });

    // A list, not a single key: during a rotation the old and new public keys
    // are published side by side until tokens signed by the old one expire.
    [HttpGet("/.well-known/jwks.json")]
    public IActionResult Jwks() => Ok(new { keys = new[] { tokens.PublicJwk } });
}
