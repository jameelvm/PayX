using PayX.Payment.Services;

namespace PayX.Payment.Jobs;

/// <summary>
/// Runs <see cref="StuckPaymentResolver"/> every <c>Resolver:Interval</c>
/// (6.5). Several Payment instances may run it at once: each resolution is a
/// read at the PSP plus an <c>xmin</c>-guarded save, so a duplicate does no
/// harm; the second save simply loses.
/// </summary>
public sealed class StuckPaymentResolverJob(IServiceScopeFactory scopes, IConfiguration config,
    ILogger<StuckPaymentResolverJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = config.GetValue("Resolver:Interval", TimeSpan.FromSeconds(15));
        var grace = config.GetValue("Resolver:GracePeriod", TimeSpan.FromSeconds(60));
        var batchSize = config.GetValue("Resolver:BatchSize", 50);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                List<string> stuck;
                using (var scope = scopes.CreateScope())
                    stuck = await scope.ServiceProvider.GetRequiredService<StuckPaymentResolver>()
                        .FindStuckAsync(grace, batchSize, stoppingToken);

                foreach (var id in stuck)
                {
                    // A fresh scope per payment: one failure doesn't poison the rest.
                    using var one = scopes.CreateScope();
                    await one.ServiceProvider.GetRequiredService<StuckPaymentResolver>().ResolveAsync(id, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Resolver pass failed; next pass in {Interval}", interval);
            }
        }
    }
}
