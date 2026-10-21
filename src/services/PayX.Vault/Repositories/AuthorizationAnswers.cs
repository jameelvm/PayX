using System.Text.Json;
using PayX.Vault.Services;
using StackExchange.Redis;

namespace PayX.Vault.Repositories;

/// <summary>
/// The PSP's definitive answer to each authorization Vault forwarded, per
/// card token and Idempotency-Key (4.7). Once an authorization completes, its
/// CVC is forgotten, so Vault could no longer rebuild the request for the
/// PSP to replay. This lets a retry whose first answer was lost on the way
/// back (Payment crashed, the network dropped it) still get that answer.
/// Holds no card data: the PSP's answer carries the last 4 digits only.
/// </summary>
public sealed class AuthorizationAnswers(IConnectionMultiplexer redis)
{
    // Long enough for any retry or resumed payment; the resolver (6.5) asks
    // the PSP directly after that.
    private static readonly TimeSpan Keep = TimeSpan.FromHours(24);

    public async Task<PspAnswer?> FindAsync(string token, string idempotencyKey)
    {
        var value = await redis.GetDatabase().StringGetAsync(Key(token, idempotencyKey));
        return value.IsNull ? null : JsonSerializer.Deserialize<PspAnswer>(value.ToString());
    }

    public Task SaveAsync(string token, string idempotencyKey, PspAnswer answer) =>
        redis.GetDatabase().StringSetAsync(Key(token, idempotencyKey), JsonSerializer.Serialize(answer), Keep);

    private static RedisKey Key(string token, string idempotencyKey) => $"vault:answer:{token}:{idempotencyKey}";
}
