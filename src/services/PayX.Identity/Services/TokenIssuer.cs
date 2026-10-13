using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PayX.Identity.Domain;

namespace PayX.Identity.Services;

public sealed record IssuedToken(string AccessToken, int ExpiresInSeconds);

/// <summary>
/// Issues the JWT a client sends on every later request. The token *is* the
/// session: it carries who the user is and what role they have, signed so it
/// can't be altered, and the Gateway (Module 2.4) checks it without calling
/// Identity or any database. That is what "stateless authentication" means.
///
/// Signed with ES256 (an asymmetric ECDSA P-256 key), not HS256 (a shared
/// secret). With a shared secret, everything that verifies tokens could also
/// mint them: a leak from the Gateway would let an attacker issue themselves
/// a Merchant token. With a key pair, only Identity holds the private key;
/// verifiers get the public key, which can check signatures but not create
/// them.
/// </summary>
public sealed class TokenIssuer
{
    private readonly SigningCredentials _credentials;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _lifetime;

    public TokenIssuer(IConfiguration config, IHostEnvironment env)
    {
        _issuer = config["Jwt:Issuer"]!;
        _audience = config["Jwt:Audience"]!;
        _lifetime = TimeSpan.FromMinutes(config.GetValue<int>("Jwt:LifetimeMinutes"));

        // Relative to the project folder, not to wherever the process was launched.
        var keyPath = Path.Combine(env.ContentRootPath, config["Jwt:SigningKeyPath"]!);
        var key = new ECDsaSecurityKey(LoadOrCreateKey(keyPath));
        // Key id = a hash of the public key (RFC 7638 thumbprint). It goes into
        // every token's header, so verifiers know which public key to use, and
        // keys can be rotated without breaking tokens signed by the old one.
        key.KeyId = Base64UrlEncoder.Encode(JsonWebKeyConverter.ConvertFromECDsaSecurityKey(key).ComputeJwkThumbprint());
        _credentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);
    }

    public IssuedToken Issue(User user)
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _issuer,          // who issued it: only Identity
            Audience = _audience,      // who it is for: the PayX APIs
            Expires = DateTime.UtcNow.Add(_lifetime),
            SigningCredentials = _credentials,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
                new Claim("role", user.Role.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ]),
        });
        return new IssuedToken(token, (int)_lifetime.TotalSeconds);
    }

    // Local development only: the key pair lives in a gitignored file, created
    // on first start and shared by every Identity instance on this machine. In
    // AWS it would be an asymmetric KMS key used through kms:Sign, so the
    // private key never leaves KMS at all.
    private static ECDsa LoadOrCreateKey(string path)
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            ecdsa.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, ecdsa.ExportPkcs8PrivateKeyPem());
        }
        return ecdsa;
    }
}
