namespace PayX.Payment.Jobs;

/// <summary>
/// Runs <see cref="OutboxPublisher"/> in the background for as long as
/// Payment runs (6.2). The request path never talks to Kafka (CLAUDE.md
/// conventions): a Kafka outage delays announcements, never payments. The
/// outbox simply grows, and drains when Kafka is back.
/// </summary>
public sealed class OutboxRelay(IServiceScopeFactory scopes, IConfiguration config, ILogger<OutboxRelay> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batchSize = config.GetValue("Outbox:BatchSize", 100);
        var idle = config.GetValue("Outbox:PollInterval", TimeSpan.FromMilliseconds(500));
        var afterError = config.GetValue("Outbox:ErrorBackoff", TimeSpan.FromSeconds(5));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var published = await scope.ServiceProvider.GetRequiredService<OutboxPublisher>()
                    .PublishBatchAsync(batchSize, stoppingToken);
                if (published > 0)
                    logger.LogInformation("Outbox relay published {Count} event(s)", published);

                // A full batch means more is waiting: go again at once.
                if (published < batchSize)
                    await Task.Delay(idle, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Outbox relay pass failed; retrying in {Delay}", afterError);
                await Task.Delay(afterError, stoppingToken);
            }
        }
    }
}
