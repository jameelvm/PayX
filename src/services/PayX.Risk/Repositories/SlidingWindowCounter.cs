using StackExchange.Redis;

namespace PayX.Risk.Repositories;

/// <summary>
/// Doc ch.2, the risk check's "real-time signals": how many payments has this
/// subject made in the last N minutes? A sliding window in a Redis sorted set
/// (<c>risk:velocity:{subject}</c>): one member per payment id, scored by the
/// time it was first seen.
///
/// Why a sliding window and not a fixed one (<c>INCR</c> + <c>EXPIRE</c> per
/// 10-minute bucket): a fixed window resets on the clock, so 5 payments at
/// 10:09 and 5 more at 10:10 are "5 per window" twice, while 10 happened in
/// one minute. A fraudster testing stolen cards bursts exactly like that. The
/// sliding window always looks back the full N minutes from now.
/// </summary>
public sealed class SlidingWindowCounter(IConnectionMultiplexer redis)
{
    // One script, so the four steps are atomic: two Risk instances recording
    // at the same moment can't both trim, add and count a half-updated set.
    //
    // TIME: Redis's clock, not each caller's. Instances whose clocks
    //   disagree by seconds would otherwise trim each other's entries early.
    // ZADD NX: the member is the payment id, so a retried assessment of the
    //   same payment is counted once, and keeps its first-seen time (no NX
    //   would move it forward and keep it in the window longer).
    // PEXPIRE: a customer who stops paying leaves no key behind.
    private const string Script = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local window = tonumber(ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - window)
        redis.call('ZADD', KEYS[1], 'NX', now, ARGV[1])
        redis.call('PEXPIRE', KEYS[1], window)
        return redis.call('ZCARD', KEYS[1])
        """;

    /// <summary>
    /// Records <paramref name="paymentId"/> for <paramref name="subject"/> and
    /// returns how many distinct payments the subject has in the window,
    /// this one included.
    /// </summary>
    public async Task<int> RecordAsync(string subject, string paymentId, TimeSpan window)
    {
        var count = await redis.GetDatabase().ScriptEvaluateAsync(Script,
            [Key(subject)], [paymentId, (long)window.TotalMilliseconds]);
        return (int)count;
    }

    public static RedisKey Key(string subject) => $"risk:velocity:{subject}";
}
