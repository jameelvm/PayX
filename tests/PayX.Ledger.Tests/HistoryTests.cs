using PayX.Ledger.Domain;
using PayX.Ledger.Services;

namespace PayX.Ledger.Tests;

// getTransactionHistory (7.6): each caller sees their own entries, newest
// first, paged by a stable cursor.
public sealed class HistoryTests
{
    [Fact]
    public async Task A_customer_sees_their_own_payment_s_life_newest_first()
    {
        var events = Events.ForNewPayment();
        await Events.Deliver(events.Event("Authorized", amount: 5_000));
        await Events.Deliver(events.Event("Captured", amount: 5_000, captured: 4_000));

        var page = await History(events.Customer, Viewer.Customer);

        Assert.Equal([EntryKind.Capture, EntryKind.Authorization], page.Items.Select(i => i.Kind));
        Assert.Equal([4_000L, 5_000L], page.Items.Select(i => i.Amount.AmountMinor));
        Assert.All(page.Items, i => Assert.Equal(events.PaymentId, i.PaymentId));
        Assert.Null(page.NextBefore);
    }

    [Fact]
    public async Task A_merchant_sees_payments_from_every_customer_and_nobody_sees_anyone_else_s()
    {
        var merchant = Guid.NewGuid();
        var alice = new Events(Guid.NewGuid(), merchant, $"pay_{Guid.NewGuid():N}");
        var bob = new Events(Guid.NewGuid(), merchant, $"pay_{Guid.NewGuid():N}");
        await Events.Deliver(alice.Event("Authorized"));
        await Events.Deliver(bob.Event("Authorized"));

        Assert.Equal(2, (await History(merchant, Viewer.Merchant)).Items.Count);
        Assert.Equal([alice.PaymentId], (await History(alice.Customer, Viewer.Customer)).Items.Select(i => i.PaymentId));
        // The merchant's id asked as a customer: a different question, no rows.
        Assert.Empty((await History(merchant, Viewer.Customer)).Items);
    }

    [Fact]
    public async Task Pages_follow_the_cursor_without_gaps_or_repeats()
    {
        var merchant = Guid.NewGuid();
        for (var i = 0; i < 7; i++)
            await Events.Deliver(new Events(Guid.NewGuid(), merchant, $"pay_{Guid.NewGuid():N}").Event("Authorized"));

        var seen = new List<long>();
        long? before = null;
        var pages = 0;
        do
        {
            var page = await History(merchant, Viewer.Merchant, before: before, limit: 3);
            seen.AddRange(page.Items.Select(i => i.EntryId));
            before = page.NextBefore;
            pages++;
        } while (before is not null);

        Assert.Equal(3, pages);   // 3 + 3 + 1
        Assert.Equal(7, seen.Distinct().Count());
        Assert.Equal(seen.OrderByDescending(id => id), seen);   // newest first, throughout
    }

    [Fact]
    public async Task A_date_range_keeps_only_what_happened_inside_it()
    {
        var events = Events.ForNewPayment();
        await Events.Deliver(events.Event("Authorized") with { OccurredAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) });
        await Events.Deliver(events.Event("Captured", captured: 5_000) with { OccurredAt = new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero) });

        var march5Onwards = await History(events.Customer, Viewer.Customer,
            from: new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal([EntryKind.Capture], march5Onwards.Items.Select(i => i.Kind));
    }

    private static async Task<HistoryPage> History(Guid caller, Viewer viewer, DateTimeOffset? from = null,
        long? before = null, int limit = 20)
    {
        await using var db = LedgerDb.NewContext();
        return await new LedgerQueries(db).HistoryAsync(caller, viewer, from, null, before, limit, default);
    }
}
