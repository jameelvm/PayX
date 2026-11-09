using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PayX.ServiceDefaults.Kafka;

/// <summary>What a consumer service implements: handle one message, or throw.</summary>
public interface IMessageHandler
{
    Task HandleAsync(ConsumeResult<string, string> message, CancellationToken ct);
}

/// <summary>
/// Thrown by a handler for a message that can never be processed (malformed,
/// unknown type, impossible values): retrying is pointless, dead-letter it now.
/// </summary>
public sealed class PoisonMessageException(string reason, Exception? inner = null) : Exception(reason, inner);

public sealed class ConsumerSettings
{
    public required string Topic { get; init; }
    public required string GroupId { get; init; }
    public required string DeadLetterTopic { get; init; }
    public required string BootstrapServers { get; init; }
    /// <summary>Attempts for an ordinary failure before dead-lettering (the first try included).</summary>
    public int MaxAttempts { get; init; } = 5;
    /// <summary>First wait between attempts; doubles each time.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);
}

public enum ProcessOutcome { Handled, DeadLettered }

/// <summary>
/// A Kafka consumer loop that can't be blocked by one bad message (6.6;
/// DESIGN decision 4's trade-off: "one bad message blocks its whole
/// partition").
///
/// <para><b>Why a poison message is so dangerous on Kafka.</b> A consumer's
/// progress is one offset per partition, not one ack per message. If message
/// 41 always throws, the consumer can't commit past it: it either skips it
/// (a lost payment event) or retries it forever while 40,000 good messages
/// for one sixth of all payments wait behind it. SQS gives you a dead-letter
/// queue for free; on Kafka you build it.</para>
///
/// <para><b>The rule here.</b> A malformed message (JSON that doesn't parse,
/// <see cref="PoisonMessageException"/>) goes to the dead-letter topic at
/// once. Any other failure is retried in place, with doubling waits, up to
/// <see cref="ConsumerSettings.MaxAttempts"/>, then dead-lettered. Either way
/// the message is copied, with why and where it came from, to the
/// dead-letter topic <em>before</em> its offset is committed: nothing is
/// ever dropped, and the partition moves on. An operator inspects the
/// dead-letter topic and replays what was fixed.</para>
///
/// <para><b>At least once.</b> The offset is committed only after the
/// message was handled (or safely dead-lettered). A crash before the commit
/// means the message is processed again, so handlers must be idempotent
/// (the Ledger dedupes by event id, 7.4).</para>
/// </summary>
public sealed class DeadLetteringConsumer<THandler>(
    ConsumerSettings settings,
    IServiceScopeFactory scopes,
    IProducer<string, string> producer,
    ILogger<DeadLetteringConsumer<THandler>> logger) : BackgroundService
    where THandler : IMessageHandler
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Consume() blocks a thread; give the loop its own instead of a pool thread.
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
                {
                    BootstrapServers = settings.BootstrapServers,
                    GroupId = settings.GroupId,
                    AutoOffsetReset = AutoOffsetReset.Earliest,   // a new group starts from the retained history
                    EnableAutoCommit = false,                      // commit only after handling (at least once)
                }).Build();
                consumer.Subscribe(settings.Topic);

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var result = consumer.Consume(ct);
                        await ProcessAsync(result, ct);
                        consumer.Commit(result);
                    }
                }
                finally
                {
                    // Leave the group on the way out (found in 7.4's replay
                    // drill). Dispose alone just drops the connection: the
                    // broker waits out the session timeout (~45 s) before
                    // handing this instance's partitions to another, and
                    // every deploy stalls them that long. Close() says
                    // goodbye, so the rebalance starts at once. Nothing to
                    // commit here: every handled message was committed above.
                    consumer.Close();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Kafka unreachable, or the dead-letter write itself failed:
                // nothing was committed, so the message comes back. Reconnect.
                logger.LogError(ex, "Consumer for {Topic} failed; reconnecting in 5 s", settings.Topic);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>Handle one message, retrying ordinary failures; dead-letter it if it can't be handled.</summary>
    public async Task<ProcessOutcome> ProcessAsync(ConsumeResult<string, string> message, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<THandler>().HandleAsync(message, ct);
                return ProcessOutcome.Handled;
            }
            catch (Exception ex) when (ex is PoisonMessageException or JsonException)
            {
                // It will fail the same way every time: don't waste the retries.
                await DeadLetterAsync(message, ex, "malformed", attempt, ct);
                return ProcessOutcome.DeadLettered;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= settings.MaxAttempts)
                {
                    await DeadLetterAsync(message, ex, "retries_exhausted", attempt, ct);
                    return ProcessOutcome.DeadLettered;
                }

                var wait = settings.RetryDelay * Math.Pow(2, attempt - 1);
                logger.LogWarning(ex, "Handling {Topic}/{Partition}@{Offset} failed (attempt {Attempt}); retrying in {Wait}",
                    message.Topic, message.Partition.Value, message.Offset.Value, attempt, wait);
                await Task.Delay(wait, ct);
            }
        }
    }

    private async Task DeadLetterAsync(ConsumeResult<string, string> message, Exception ex, string reason,
        int attempts, CancellationToken ct)
    {
        // The original key, value and headers, plus where it came from and
        // why it failed: enough to understand it, fix it and replay it.
        var headers = new Headers();
        foreach (var header in message.Message.Headers ?? [])
            headers.Add(header.Key, header.GetValueBytes());
        headers.Add("dlq-reason", Encoding.UTF8.GetBytes(reason));             // malformed | retries_exhausted
        headers.Add("dlq-exception", Encoding.UTF8.GetBytes(ex.GetType().Name));
        headers.Add("dlq-error", Encoding.UTF8.GetBytes(ex.Message.Length > 500 ? ex.Message[..500] : ex.Message));
        headers.Add("dlq-attempts", Encoding.UTF8.GetBytes(attempts.ToString()));
        headers.Add("dlq-source", Encoding.UTF8.GetBytes($"{message.Topic}/{message.Partition.Value}@{message.Offset.Value}"));
        headers.Add("dlq-consumer-group", Encoding.UTF8.GetBytes(settings.GroupId));
        headers.Add("dlq-failed-at", Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));

        // Awaited: only once the copy is safely in the dead-letter topic may
        // the original's offset be committed. If this throws, nothing is
        // committed and the message is retried after reconnecting.
        await producer.ProduceAsync(settings.DeadLetterTopic,
            new Message<string, string> { Key = message.Message.Key, Value = message.Message.Value, Headers = headers }, ct);

        logger.LogError(ex, "Dead-lettered {Topic}/{Partition}@{Offset} (key {Key}) after {Attempts} attempt(s)",
            message.Topic, message.Partition.Value, message.Offset.Value, message.Message.Key, attempts);
    }
}
