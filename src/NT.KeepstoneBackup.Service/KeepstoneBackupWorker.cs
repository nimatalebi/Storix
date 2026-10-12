using NT.KeepstoneBackup.Core.Engine;

namespace NT.KeepstoneBackup.Service;

/// <summary>Hosts the <see cref="BackupScheduler"/> inside the Windows service.</summary>
public sealed class KeepstoneBackupWorker(BackupScheduler scheduler, ILogger<KeepstoneBackupWorker> logger) : BackgroundService
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
