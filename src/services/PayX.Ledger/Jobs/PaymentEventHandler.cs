using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using PayX.Contracts;
using PayX.Contracts.Events;
using PayX.Ledger.Domain;
using PayX.Ledger.Repositories;
using PayX.Ledger.Services;
using PayX.ServiceDefaults.Kafka;

namespace PayX.Ledger.Jobs;

/// <summary>
/// Books payment events from <c>payx.payments</c> (7.3). Doc ch.2: "a message
/// is only marked as consumed after the transaction is … recorded in the
/// wallet and ledger". The loop around this handler
/// (<see cref="DeadLetteringConsumer{THandler}"/>, 6.6) commits the Kafka
/// offset only after this method returns, and this method returns only
/// after the entry's database transaction committed. A crash in between
/// means the event is read again: at least once, never at most once.
///
/// <para>Throwing is how this handler says "not booked": a
/// <see cref="PoisonMessageException"/> for an event that can never be booked
/// (dead-lettered at once), anything else to be retried, then dead-lettered.</para>
/// </summary>
public sealed class PaymentEventHandler(LedgerDbContext db, LedgerWriter writer, ILogger<PaymentEventHandler> logger)
    : IMessageHandler
{
    public async Task HandleAsync(ConsumeResult<string, string> message, CancellationToken ct)
    {
        var e = Read(message.Message.Value);

        if (PostingRules.For(e) is not { } booking)
        {
            if (!PostingRules.NotBooked.Contains(e.Status))
                // A status this Ledger doesn't know: a newer Payment? Skipped
                // loudly, not dead-lettered: most new statuses move no money,
                // and one that does needs a Ledger release anyway.
                logger.LogWarning("Event {EventId} has status {Status}, which the ledger doesn't know; skipped",
                    e.EventId, e.Status);
            return;
        }

        if (booking.Kind is EntryKind.Capture or EntryKind.Void
            && !await db.JournalEntries.AnyAsync(j => j.PaymentId == e.PaymentId && j.Kind == EntryKind.Authorization, ct))
            // Its authorization should have come first: same payment, same
            // partition, in order. If it isn't booked, it was dead-lettered
            // (or predates the topic's retention). Booking this anyway would
            // release a hold that was never placed. Retried, then parked
            // next to its predecessor, to be replayed after it.
            throw new MissingPredecessorException(
                $"{e.EventType} for {e.PaymentId} arrived but its authorization is not in the ledger");

        var result = await writer.PostAsync(new EntryDraft(booking.Kind, e.PaymentId, e.CustomerId, e.MerchantId,
            booking.Amount, e.OccurredAt, booking.Legs, e.EventId), ct);

        // Neither is an error, and neither is retried: the books already hold
        // this step, so acknowledging the message is exactly right (7.4).
        if (result.Outcome == PostOutcome.Duplicate)
            logger.LogInformation("Event {EventId} ({EventType}, {PaymentId}) was already booked; redelivery ignored",
                e.EventId, e.EventType, e.PaymentId);
        else if (result.Outcome == PostOutcome.StepAlreadyBooked)
            logger.LogWarning("{EventType} for {PaymentId} is already booked under another event id; {EventId} ignored",
                e.EventType, e.PaymentId, e.EventId);
    }

    /// <summary>
    /// Parse and check the event. Anything structurally wrong is poison: it
    /// will be wrong on every retry, so it goes to the dead-letter topic at
    /// once with the reason. Missing fields matter most: System.Text.Json
    /// fills a missing field with null or 0 rather than failing, and a ledger
    /// must never book a silent zero.
    /// </summary>
    public static PaymentEvent Read(string json)
    {
        PaymentEvent? e;
        try
        {
            e = JsonSerializer.Deserialize<PaymentEvent>(json, PaymentEvent.Json);
        }
        catch (ArgumentException ex)   // Money's constructor: unknown currency, amount out of range
        {
            throw new PoisonMessageException($"Invalid amount: {ex.Message}", ex);
        }

        if (e is null)
            throw new PoisonMessageException("Empty event");
        if (e.EventId == Guid.Empty || string.IsNullOrEmpty(e.PaymentId) || string.IsNullOrEmpty(e.Status)
            || e.CustomerId == Guid.Empty || e.MerchantId == Guid.Empty || e.Amount is null || e.Sequence <= 0)
            throw new PoisonMessageException("Event is missing required fields");
        if (e.Amount.AmountMinor <= 0)
            throw new PoisonMessageException($"Amount must be positive, was {e.Amount.AmountMinor}");
        if (e.Status == "Captured" && e.CapturedAmountMinor is not (> 0) )
            throw new PoisonMessageException("A capture must say how much was captured");
        if (e.CapturedAmountMinor > e.Amount.AmountMinor)
            throw new PoisonMessageException(
                $"Captured {e.CapturedAmountMinor} is more than the authorized {e.Amount.AmountMinor}");
        return e;
    }
}

public sealed class MissingPredecessorException(string message) : InvalidOperationException(message);
