using Microsoft.EntityFrameworkCore;
using PayX.Ledger.Domain;
using PayX.Ledger.Jobs;
using PayX.ServiceDefaults.Kafka;

namespace PayX.Ledger.Tests;

// The consumer's handler (7.3) against the real payx_ledger database. The
// loop around it (commit after handling, dead-lettering) is tested in 6.6.
public sealed class PaymentEventHandlerTests
{
    [Fact]
    public async Task A_payment_s_life_is_booked_entry_by_entry()
    {
        var events = Events.ForNewPayment();

        foreach (var e in new[]
                 {
                     events.Event("RiskApproved"),
                     events.Event("Authorized", amount: 5_000),
                     events.Event("Captured", amount: 5_000, captured: 5_000),
                 })
            await Events.Deliver(e);

        await using var db = LedgerDb.NewContext();
        var entries = await db.JournalEntries.Include(j => j.Postings)
            .Where(j => j.PaymentId == events.PaymentId).OrderBy(j => j.Id).ToListAsync();
        Assert.Equal([EntryKind.Authorization, EntryKind.Capture], entries.Select(j => j.Kind));   // RiskApproved: nothing
        Assert.Equal([2, 4], entries.Select(j => j.Postings.Count));
        Assert.All(entries, j => Assert.Equal(events.Customer, j.CustomerId));
    }

    [Fact]
    public async Task A_capture_whose_authorization_is_not_booked_is_refused_not_guessed()
    {
        var events = Events.ForNewPayment();
        events.Event("Authorized");   // produced, but never delivered (say, dead-lettered)

        await Assert.ThrowsAsync<MissingPredecessorException>(() =>
            Events.Deliver(events.Event("Captured", captured: 5_000)));

        await using var db = LedgerDb.NewContext();
        Assert.False(await db.JournalEntries.AnyAsync(j => j.PaymentId == events.PaymentId));
    }

    [Theory]
    [InlineData("""{"eventId":"00000000-0000-0000-0000-000000000000","paymentId":"pay_x","status":"Authorized","sequence":1}""")]   // no amount, empty ids
    [InlineData("""{"eventId":"0193f1a2-0000-7000-8000-000000000001","eventType":"payment.authorized","paymentId":"pay_x","sequence":1,"status":"Authorized","merchantId":"0193f1a2-0000-7000-8000-000000000002","customerId":"0193f1a2-0000-7000-8000-000000000003","amount":{"amountMinor":500,"currency":"XYZ"},"occurredAt":"2026-11-09T00:00:00Z"}""")]   // unknown currency
    [InlineData("""{"eventId":"0193f1a2-0000-7000-8000-000000000001","eventType":"payment.captured","paymentId":"pay_x","sequence":3,"status":"Captured","merchantId":"0193f1a2-0000-7000-8000-000000000002","customerId":"0193f1a2-0000-7000-8000-000000000003","amount":{"amountMinor":500,"currency":"USD"},"capturedAmountMinor":900,"occurredAt":"2026-11-09T00:00:00Z"}""")]   // captured more than authorized
    [InlineData("""{"eventId":"0193f1a2-0000-7000-8000-000000000001","eventType":"payment.captured","paymentId":"pay_x","sequence":3,"status":"Captured","merchantId":"0193f1a2-0000-7000-8000-000000000002","customerId":"0193f1a2-0000-7000-8000-000000000003","amount":{"amountMinor":500,"currency":"USD"},"occurredAt":"2026-11-09T00:00:00Z"}""")]   // capture without an amount
    [InlineData("null")]
    public async Task An_event_that_can_never_be_booked_is_poison(string json)
    {
        await using var db = LedgerDb.NewContext();

        await Assert.ThrowsAsync<PoisonMessageException>(() =>
            Events.Handler(db).HandleAsync(Events.Message("pay_x", json), default));
    }

    [Fact]
    public async Task Text_that_is_not_json_fails_as_json_so_the_loop_dead_letters_it()
    {
        await using var db = LedgerDb.NewContext();

        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() =>
            Events.Handler(db).HandleAsync(Events.Message("pay_x", "this is not json"), default));
    }

    [Fact]
    public async Task A_status_the_ledger_does_not_know_is_skipped()
    {
        var events = Events.ForNewPayment();

        await Events.Deliver(events.Event("Refunded"));   // a later phase's status, today unknown

        await using var db = LedgerDb.NewContext();
        Assert.False(await db.JournalEntries.AnyAsync(j => j.PaymentId == events.PaymentId));
    }
}
