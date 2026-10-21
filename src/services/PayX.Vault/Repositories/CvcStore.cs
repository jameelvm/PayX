using System.Text.Json;
using PayX.Vault.Domain;
using PayX.Vault.Services;
using StackExchange.Redis;

namespace PayX.Vault.Repositories;

/// <summary>
/// Holds a card's CVC from checkout until the first authorization, and no
/// longer. PCI DSS forbids keeping the CVC once authorization is done, even
/// encrypted, so it never goes near DynamoDB. Here it is:
/// <list type="bullet">
/// <item>encrypted (same envelope scheme as the card, 3.6): Redis can be
/// snapshotted to disk and replicated, so a plain CVC there would be stored
/// card data like any other;</item>
/// <item>short-lived: a TTL removes it if the payment never happens;</item>
/// <item>read once: <see cref="TakeAsync"/> deletes it in the same command.</item>
/// </list>
/// </summary>
public sealed class CvcStore(IConnectionMultiplexer redis, CardEncryptor encryptor, IConfiguration config)
{
    private readonly TimeSpan _ttl = config.GetValue<TimeSpan?>("Vault:CvcTtl")
        ?? throw new InvalidOperationException("Missing configuration: Vault:CvcTtl");

    public async Task HoldAsync(string token, string cvc, CancellationToken ct)
    {
        var encrypted = await encryptor.EncryptAsync(cvc, Context(token), ct);
        await redis.GetDatabase().StringSetAsync(Key(token), JsonSerializer.Serialize(encrypted), _ttl);
    }

    /// <summary>The CVC if it is still held, and it is gone afterwards either way.</summary>
    public async Task<string?> TakeAsync(string token, CancellationToken ct)
    {
        // GETDEL: read and delete atomically. A GET then a DEL would let two
        // concurrent authorizations both read it.
        var value = await redis.GetDatabase().StringGetDeleteAsync(Key(token));
        if (value.IsNull)
            return null;

        var encrypted = JsonSerializer.Deserialize<EncryptedCard>(value.ToString())!;
        return await encryptor.DecryptAsync(encrypted, Context(token), ct);
    }

    private static RedisKey Key(string token) => $"vault:cvc:{token}";

    // A record id of its own, not the card's token: the card's ciphertext
    // can't be decrypted as a CVC, or the CVC's as a card number.
    private static string Context(string token) => $"{token}:cvc";
}
