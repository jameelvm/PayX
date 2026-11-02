using PayX.Risk.Repositories;
using StackExchange.Redis;

namespace PayX.Risk.Tests;

// Against the real local Redis (docker compose up redis, port 6381), like
// Vault's CVC tests: the script's atomicity and Redis's TIME are the point,
// and a fake would test neither. Each test uses its own subject.
public sealed class SlidingWindowCounterTests
{
    private static readonly IConnectionMultiplexer Redis = ConnectionMultiplexer.Connect("localhost:6381");
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    private readonly SlidingWindowCounter _counter = new(Redis);
    private readonly string _subject = $"test:{Guid.NewGuid():N}";

    [Fact]
    public async Task Counts_distinct_payments_including_this_one()
    {
        Assert.Equal(1, await _counter.RecordAsync(_subject, "pay_1", TenMinutes));
        Assert.Equal(2, await _counter.RecordAsync(_subject, "pay_2", TenMinutes));
        Assert.Equal(3, await _counter.RecordAsync(_subject, "pay_3", TenMinutes));
    }

    [Fact]
    public async Task The_same_payment_assessed_again_is_counted_once()
    {
        // Payment retries its Risk call (timeout, 5.5): not a second payment.
        await _counter.RecordAsync(_subject, "pay_1", TenMinutes);
        await _counter.RecordAsync(_subject, "pay_1", TenMinutes);

        Assert.Equal(2, await _counter.RecordAsync(_subject, "pay_2", TenMinutes));
    }

    [Fact]
    public async Task Subjects_are_counted_separately()
    {
        await _counter.RecordAsync(_subject, "pay_1", TenMinutes);
        await _counter.RecordAsync(_subject, "pay_2", TenMinutes);

        Assert.Equal(1, await _counter.RecordAsync($"{_subject}:other", "pay_3", TenMinutes));
    }

    [Fact]
    public async Task Payments_older_than_the_window_drop_out()
    {
        var window = TimeSpan.FromSeconds(1);
        await _counter.RecordAsync(_subject, "pay_1", window);
        await _counter.RecordAsync(_subject, "pay_2", window);

        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        // Sliding: the old two have left; a fixed bucket might still hold them.
        Assert.Equal(1, await _counter.RecordAsync(_subject, "pay_3", window));
    }

    [Fact]
    public async Task The_key_expires_with_the_window()
    {
        await _counter.RecordAsync(_subject, "pay_1", TenMinutes);

        var ttl = await Redis.GetDatabase().KeyTimeToLiveAsync(SlidingWindowCounter.Key(_subject));
        Assert.InRange(ttl!.Value, TimeSpan.FromMinutes(9), TenMinutes);
    }
}
