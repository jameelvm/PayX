using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PayX.Contracts;
using PayX.Contracts.Events;
using PayX.Payment.Jobs;
using PayX.Payment.Repositories;
using PayX.ServiceDefaults;

namespace PayX.Payment.Tests;

// The outbox relay (6.2) against the real payx_payments database and the real
// local Kafka broker (localhost:9094). The Payment container's own relay may
// publish the same rows first; either way the events must reach Kafka once
// marked, keyed by payment id and in order.
public sealed class OutboxRelayTests
{
    private const string Db = "Host=localhost;Port=5434;Database=payx_payments;Username=payx_payments;Password=payx_payments_dev";
    private static readonly IProducer<string, string> Producer = new ProducerBuilder<string, string>(
        KafkaExtensions.ProducerConfig(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Kafka:BootstrapServers"] = "localhost:9094" }).Build())).Build();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public OutboxRelayTests()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    [Fact]
    public async Task Published_events_reach_kafka_keyed_by_payment_and_in_order()
    {
        var paymentId = await PaymentWithTwoEvents();

        await PublishUntilDone(paymentId);

        var messages = await ReadFromKafka(paymentId, expected: 2);
        Assert.Equal(["payment.riskapproved", "payment.authorized"],
            messages.Select(m => Encoding.UTF8.GetString(m.Message.Headers.GetLastBytes("event-type"))));
        Assert.Equal([1, 2], messages.Select(m => JsonSerializer.Deserialize<PaymentEvent>(m.Message.Value, Json)!.Sequence));
        Assert.Single(messages.Select(m => m.Partition.Value).Distinct());   // one payment, one partition
    }

    [Fact]
    public async Task A_published_event_is_never_published_again()
    {
        var paymentId = await PaymentWithTwoEvents();
        await PublishUntilDone(paymentId);
        var stamped = await PublishedAt(paymentId);

        await NewPublisher(out var db).PublishBatchAsync(1000, default);
        await db.DisposeAsync();

        Assert.Equal(stamped, await PublishedAt(paymentId));
    }

    [Fact]
    public async Task While_one_relay_holds_the_lock_another_publishes_nothing()
    {
        var paymentId = await PaymentWithTwoEvents();

        // Another instance's relay, mid-pass: it holds the advisory lock.
        await using var other = new NpgsqlConnection(Db);
        await other.OpenAsync();
        await using var otherTx = await other.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({OutboxPublisher.RelayLock})", other, otherTx))
            await take.ExecuteNonQueryAsync();

        Assert.Equal(0, await NewPublisher(out var db).PublishBatchAsync(1000, default));
        await db.DisposeAsync();
        Assert.All(await PublishedAt(paymentId), p => Assert.Null(p));

        await otherTx.RollbackAsync();   // that relay finishes; the lock is free
        await PublishUntilDone(paymentId);
        Assert.All(await PublishedAt(paymentId), p => Assert.NotNull(p));
    }

    private static async Task<string> PaymentWithTwoEvents()
    {
        var payment = new Domain.Payment(Guid.NewGuid(), Guid.NewGuid(), new Money(5_000, "USD"), "tok_test");
        await using var db = NewContext();
        db.Payments.Add(payment);
        payment.ApproveRisk();
        await db.SaveChangesAsync();
        payment.Authorize("auth_1", "A1B2C3");
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private static async Task PublishUntilDone(string paymentId)
    {
        for (var pass = 0; pass < 50 && (await PublishedAt(paymentId)).Any(p => p is null); pass++)
        {
            await NewPublisher(out var db).PublishBatchAsync(1000, default);
            await db.DisposeAsync();
        }
        Assert.All(await PublishedAt(paymentId), p => Assert.NotNull(p));
    }

    private static async Task<List<DateTimeOffset?>> PublishedAt(string paymentId)
    {
        await using var db = NewContext();
        return await db.OutboxMessages.Where(o => o.Key == paymentId).OrderBy(o => o.Id)
            .Select(o => o.PublishedAt).ToListAsync();
    }

    private static async Task<List<ConsumeResult<string, string>>> ReadFromKafka(string paymentId, int expected)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = "localhost:9094",
            GroupId = $"test-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(Topics.Payments);

        var found = new List<ConsumeResult<string, string>>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (found.Count < expected && DateTime.UtcNow < deadline)
        {
            var result = await Task.Run(() => consumer.Consume(TimeSpan.FromSeconds(1)));
            if (result?.Message?.Key == paymentId)
                found.Add(result);
        }
        consumer.Close();
        Assert.Equal(expected, found.Count);
        return found;
    }

    private static OutboxPublisher NewPublisher(out PaymentsDbContext db)
    {
        db = NewContext();
        return new OutboxPublisher(db, Producer, TimeProvider.System);
    }

    private static PaymentsDbContext NewContext() => new(new DbContextOptionsBuilder<PaymentsDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}
