using Microsoft.EntityFrameworkCore;
using PayX.Payment.Repositories;
using PayX.Payment.Services;

namespace PayX.Payment.Tests;

// Payment's Idempotency-Key claims against the real payx_payments database.
public sealed class IdempotencyServiceTests
{
    private readonly string _scope = Guid.NewGuid().ToString();
    private readonly string _key = $"key-{Guid.NewGuid():N}";

    public IdempotencyServiceTests()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    [Fact]
    public async Task First_use_of_a_key_claims_it()
    {
        var claim = await Service().ClaimAsync(_scope, _key, "hash-a", default);

        Assert.Equal(ClaimOutcome.Claimed, claim.Outcome);
    }

    [Fact]
    public async Task A_duplicate_while_the_first_is_running_is_told_it_is_in_progress()
    {
        await Service().ClaimAsync(_scope, _key, "hash-a", default);

        Assert.Equal(ClaimOutcome.InProgress, (await Service().ClaimAsync(_scope, _key, "hash-a", default)).Outcome);
    }

    [Fact]
    public async Task After_completion_a_duplicate_gets_the_stored_answer()
    {
        var service = Service();
        var first = await service.ClaimAsync(_scope, _key, "hash-a", default);
        await service.CompleteAsync(first.Record!, 201, """{"id":"pay_1"}""", "/api/payments/pay_1", default);

        var again = await Service().ClaimAsync(_scope, _key, "hash-a", default);

        Assert.Equal(ClaimOutcome.Replay, again.Outcome);
        Assert.Equal((201, """{"id":"pay_1"}""", "/api/payments/pay_1"),
            (again.Record!.ResponseStatus, again.Record.ResponseBody, again.Record.ResponseLocation));
    }

    [Fact]
    public async Task The_same_key_for_a_different_request_is_a_mismatch()
    {
        await Service().ClaimAsync(_scope, _key, "hash-a", default);

        Assert.Equal(ClaimOutcome.Mismatch, (await Service().ClaimAsync(_scope, _key, "hash-b", default)).Outcome);
    }

    [Fact]
    public async Task Keys_belong_to_their_caller()
    {
        await Service().ClaimAsync(_scope, _key, "hash-a", default);

        var otherCaller = await Service().ClaimAsync(Guid.NewGuid().ToString(), _key, "hash-a", default);

        Assert.Equal(ClaimOutcome.Claimed, otherCaller.Outcome);
    }

    [Fact]
    public async Task Ten_simultaneous_claims_of_one_key_have_exactly_one_winner()
    {
        var claims = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => Service().ClaimAsync(_scope, _key, "hash-a", default))));

        Assert.Single(claims, c => c.Outcome == ClaimOutcome.Claimed);
        Assert.Equal(9, claims.Count(c => c.Outcome == ClaimOutcome.InProgress));
    }

    private static IdempotencyService Service() => new(NewContext());

    private static PaymentsDbContext NewContext() => new(new DbContextOptionsBuilder<PaymentsDbContext>()
        .UseNpgsql("Host=localhost;Port=5434;Database=payx_payments;Username=payx_payments;Password=payx_payments_dev")
        .UseSnakeCaseNamingConvention()
        .Options);
}
