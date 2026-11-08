using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayX.ServiceDefaults;
using PayX.ServiceDefaults.Kafka;

namespace PayX.Payment.Tests;

// The dead-lettering consumer (6.6, ServiceDefaults) against the real local
// Kafka broker. Each test gets its own topics, created here and deleted
// after, so nothing touches payx.payments. (It lives in this project only
// because this is where the Kafka integration tests already run.)
public sealed class DeadLetteringConsumerTests : IAsyncLifetime
{
    private const string Bootstrap = "localhost:9094";
    private static readonly IProducer<string, string> Producer = new ProducerBuilder<string, string>(
        KafkaExtensions.ProducerConfig(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Kafka:BootstrapServers"] = Bootstrap }).Build())).Build();

    private readonly string _topic = $"test.consumer.{Guid.NewGuid():N}";
    private string Dlq => _topic + ".dlq";
    private readonly RecordingHandler _handler = new();

    public async Task InitializeAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Bootstrap }).Build();
        await admin.CreateTopicsAsync([
            new TopicSpecification { Name = _topic, NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = Dlq, NumPartitions = 1, ReplicationFactor = 1 },
        ]);
    }

    public async Task DisposeAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Bootstrap }).Build();
        await admin.DeleteTopicsAsync([_topic, Dlq]);
    }

    [Fact]
    public async Task A_poison_message_is_dead_lettered_and_the_partition_moves_on()
    {
        await Producer.ProduceAsync(_topic, new Message<string, string> { Key = "pay_1", Value = """{"n":1}""" });
        await Producer.ProduceAsync(_topic, new Message<string, string> { Key = "pay_2", Value = "this is not json" });
        await Producer.ProduceAsync(_topic, new Message<string, string> { Key = "pay_3", Value = """{"n":3}""" });

        var group = $"test-{Guid.NewGuid():N}";
        var consumer = Consumer(group);
        await consumer.StartAsync(default);
        await WaitUntil(() => _handler.Handled.Count == 2);
        await consumer.StopAsync(default);

        // The good messages either side of the poison one were both handled.
        Assert.Equal(["pay_1", "pay_3"], _handler.Handled);

        // The poison one is in the dead-letter topic, untouched, with why and where.
        var dead = Assert.Single(await ReadAll(Dlq));
        Assert.Equal(("pay_2", "this is not json"), (dead.Message.Key, dead.Message.Value));
        Assert.Equal("malformed", Header(dead, "dlq-reason"));
        Assert.Equal($"{_topic}/0@1", Header(dead, "dlq-source"));
        Assert.Equal("1", Header(dead, "dlq-attempts"));   // malformed: not retried

        // And the group's position is past all three: nothing blocks, nothing is re-read.
        Assert.Equal(3, Committed(group));
    }

    [Fact]
    public async Task An_ordinary_failure_is_retried_in_place_and_not_dead_lettered()
    {
        _handler.FailNextTimes = 2;   // e.g. the database blinked

        var outcome = await Consumer($"test-{Guid.NewGuid():N}").ProcessAsync(Fake("pay_1", """{"n":1}"""), default);

        Assert.Equal(ProcessOutcome.Handled, outcome);
        Assert.Equal(3, _handler.Attempts);
        Assert.Empty(await ReadAll(Dlq));
    }

    [Fact]
    public async Task A_failure_that_never_clears_is_dead_lettered_after_the_last_attempt()
    {
        _handler.FailNextTimes = int.MaxValue;

        var outcome = await Consumer($"test-{Guid.NewGuid():N}").ProcessAsync(Fake("pay_9", """{"n":9}"""), default);

        Assert.Equal(ProcessOutcome.DeadLettered, outcome);
        Assert.Equal(3, _handler.Attempts);   // MaxAttempts
        var dead = Assert.Single(await ReadAll(Dlq));
        Assert.Equal(("retries_exhausted", "InvalidOperationException", "3", "kept failing"),
            (Header(dead, "dlq-reason"), Header(dead, "dlq-exception"), Header(dead, "dlq-attempts"), Header(dead, "dlq-error")));
        Assert.Equal("payment.authorized", Header(dead, "event-type"));   // original headers kept
    }

    private DeadLetteringConsumer<RecordingHandler> Consumer(string group)
    {
        var services = new ServiceCollection().AddSingleton(_handler).BuildServiceProvider();
        return new DeadLetteringConsumer<RecordingHandler>(new ConsumerSettings
        {
            Topic = _topic,
            GroupId = group,
            DeadLetterTopic = Dlq,
            BootstrapServers = Bootstrap,
            MaxAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(10),
        }, services.GetRequiredService<IServiceScopeFactory>(), Producer,
            NullLogger<DeadLetteringConsumer<RecordingHandler>>.Instance);
    }

    private ConsumeResult<string, string> Fake(string key, string value) => new()
    {
        Topic = _topic,
        Partition = new Partition(0),
        Offset = new Offset(7),
        Message = new Message<string, string>
        {
            Key = key,
            Value = value,
            Headers = new Headers { { "event-type", Encoding.UTF8.GetBytes("payment.authorized") } },
        },
    };

    private static async Task<List<ConsumeResult<string, string>>> ReadAll(string topic)
    {
        using var reader = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = Bootstrap,
            GroupId = $"reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        reader.Assign(new TopicPartitionOffset(topic, 0, Offset.Beginning));
        var all = new List<ConsumeResult<string, string>>();
        while (await Task.Run(() => reader.Consume(TimeSpan.FromSeconds(3))) is { } r && !r.IsPartitionEOF)
            all.Add(r);
        return all;
    }

    private long Committed(string group)
    {
        using var c = new ConsumerBuilder<string, string>(new ConsumerConfig { BootstrapServers = Bootstrap, GroupId = group }).Build();
        return c.Committed([new TopicPartition(_topic, 0)], TimeSpan.FromSeconds(10)).Single().Offset.Value;
    }

    private static string Header(ConsumeResult<string, string> r, string name) =>
        Encoding.UTF8.GetString(r.Message.Headers.GetLastBytes(name));

    private static async Task WaitUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.True(done(), "timed out");
    }

    public sealed class RecordingHandler : IMessageHandler
    {
        public ConcurrentQueue<string> Handled { get; } = new();
        public int Attempts { get; private set; }
        public int FailNextTimes { get; set; }

        public Task HandleAsync(ConsumeResult<string, string> message, CancellationToken ct)
        {
            Attempts++;
            JsonDocument.Parse(message.Message.Value).Dispose();   // malformed → JsonException → poison
            if (FailNextTimes-- > 0)
                throw new InvalidOperationException("kept failing");
            Handled.Enqueue(message.Message.Key);
            return Task.CompletedTask;
        }
    }
}
