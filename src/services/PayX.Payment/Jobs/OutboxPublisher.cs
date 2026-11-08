using System.Text;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using PayX.Payment.Repositories;

namespace PayX.Payment.Jobs;

/// <summary>
/// One pass of the outbox relay (6.2): take the oldest unpublished events,
/// publish them to Kafka in order, mark them published. Doc ch.2,
/// "Transaction completion"; DESIGN decision 5.
///
/// Delivery is <b>at least once</b>, never at most once: an event is marked
/// published only after Kafka confirmed it. If the process dies between the
/// two, the next pass publishes it again, so consumers must dedupe by
/// <c>eventId</c> (7.4). The other order (mark, then publish) could lose an
/// event, and a lost "payment.captured" is money the ledger never sees.
/// </summary>
public sealed class OutboxPublisher(PaymentsDbContext db, IProducer<string, string> producer, TimeProvider clock)
{
    // Any constant works; it only has to be the same in every Payment instance.
    public const long RelayLock = 62_001;

    /// <returns>How many events were published; 0 also when another instance holds the relay lock.</returns>
    public async Task<int> PublishBatchAsync(int batchSize, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // One relay at a time, across all Payment instances. Two relays each
        // taking a batch would race: instance B could publish a payment's
        // "captured" before instance A publishes its "authorized", and the
        // consumer would see them out of order. A transaction-scoped advisory
        // lock lets exactly one instance through; the others skip this pass,
        // and the lock frees itself on commit, or if the holder dies.
        var leader = await db.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({RelayLock}) AS \"Value\"").SingleAsync(ct);
        if (!leader)
            return 0;

        var batch = await db.OutboxMessages
            .Where(o => o.PublishedAt == null).OrderBy(o => o.Id).Take(batchSize).ToListAsync(ct);

        var published = 0;
        foreach (var message in batch)
        {
            try
            {
                // Awaited one by one: the next event goes out only after Kafka
                // confirmed this one, which keeps the outbox order on the wire.
                await producer.ProduceAsync(message.Topic, new Message<string, string>
                {
                    Key = message.Key,                 // payment id → partition → per-payment order
                    Value = message.Payload,
                    Headers = new Headers
                    {
                        { "event-type", Encoding.UTF8.GetBytes(message.EventType) },
                        { "event-id", Encoding.UTF8.GetBytes(message.EventId.ToString()) },
                    },
                }, ct);
            }
            catch (ProduceException<string, string>) when (published > 0)
            {
                // Kafka refused or became unreachable mid-batch: keep what was
                // confirmed and retry the rest on the next pass. Skipping ahead
                // would publish later events before this one.
                break;
            }

            message.MarkPublished(clock.GetUtcNow());
            published++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return published;
    }
}
