using NT.Storix.Core.Engine;

namespace NT.Storix.Service;

/// <summary>Hosts the <see cref="BackupScheduler"/> inside the Windows service.</summary>
public sealed class StorixWorker(BackupScheduler scheduler, ILogger<StorixWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await scheduler.RunAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogCritical(ex, "The scheduler crashed.");

            // Exit with a non-zero code so the Service Control Manager recovery options restart the service.
            Environment.Exit(1);
        }
    }
}
