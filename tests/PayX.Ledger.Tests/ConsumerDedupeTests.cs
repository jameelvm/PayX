using Microsoft.EntityFrameworkCore;
using PayX.Ledger.Domain;

namespace PayX.Ledger.Tests;

// Idempotent consumer (7.4): the same event delivered any number of times,
// one after another or at the same moment, is booked exactly once.
public sealed class ConsumerDedupeTests
{
    [Fact]
    public async Task A_redelivered_event_is_booked_once()
    {
        var events = Events.ForNewPayment();
        var authorized = events.Event("Authorized");

        // The consumer crashed after booking, before committing the offset:
        // Kafka delivers the same message again. And once more for luck.
        for (var i = 0; i < 3; i++)
            await Events.Deliver(authorized);

        await using var db = LedgerDb.NewContext();
        Assert.Equal(1, await db.JournalEntries.CountAsync(j => j.PaymentId == events.PaymentId));
        var processed = await db.ProcessedEvents.SingleAsync(p => p.EventId == authorized.EventId);
        Assert.Equal(events.PaymentId, processed.PaymentId);   // and it points at the entry it made
        Assert.True(await db.JournalEntries.AnyAsync(j => j.Id == processed.EntryId));
    }

    [Fact]
    public async Task Simultaneous_deliveries_of_one_event_book_it_once()
    {
        // Two consumers holding the same message at once (a rebalance mid-
        // processing). Both pass the "already processed?" check; the primary
        // key lets only one commit, and the loser reports a duplicate, not an error.
        var events = Events.ForNewPayment();
        var authorized = events.Event("Authorized");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Events.Deliver(authorized))));

        await using var db = LedgerDb.NewContext();
        Assert.Equal(1, await db.JournalEntries.CountAsync(j => j.PaymentId == events.PaymentId));
        Assert.Equal(1, await db.ProcessedEvents.CountAsync(p => p.PaymentId == events.PaymentId));
    }

    [Fact]
    public async Task The_same_step_under_a_new_event_id_is_not_booked_again()
    {
        // A replay by hand that minted a new event id (or a producer bug):
        // the event id is new, but the payment's capture is already booked.
        var events = Events.ForNewPayment();
        await Events.Deliver(events.Event("Authorized"));
        var capture = events.Event("Captured", captured: 5_000);
        await Events.Deliver(capture);

        await Events.Deliver(capture with { EventId = Guid.CreateVersion7() });

        await using var db = LedgerDb.NewContext();
        Assert.Equal(1, await db.JournalEntries.CountAsync(j => j.PaymentId == events.PaymentId && j.Kind == EntryKind.Capture));
    }

    [Fact]
    public async Task Events_that_book_nothing_leave_no_trace()
    {
        var events = Events.ForNewPayment();
        await Events.Deliver(events.Event("RiskApproved"));

        await using var db = LedgerDb.NewContext();
        Assert.False(await db.ProcessedEvents.AnyAsync(p => p.PaymentId == events.PaymentId));
    }
}
