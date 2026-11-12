using PayX.PspSimulator.Services;

namespace PayX.PspSimulator.Jobs;

/// <summary>
/// Runs a settlement every <c>Settlement:Interval</c> (8.1).
///
/// A real PSP settles once a day (the doc's "end-of-day settlement file").
/// Locally the "day" is set to a few minutes in appsettings, so you can
/// watch files appear without waiting until midnight. Set it to 1.00:00:00
/// for a real day.
/// </summary>
public sealed class SettlementJob(IServiceScopeFactory scopes, IConfiguration config, ILogger<SettlementJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = config.GetValue("Settlement:Interval", TimeSpan.FromDays(1));
        logger.LogInformation("Settlement runs every {Interval}", interval);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<Settler>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // E.g. S3 down. Nothing is lost: the closed batch waits in the
                // DB with no UploadedAt and is uploaded on the next tick.
                logger.LogWarning(ex, "Settlement run failed; trying again in {Interval}", interval);
            }
        }
    }
}
