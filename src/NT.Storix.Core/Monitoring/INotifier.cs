using NT.Storix.Core.Models;

namespace NT.Storix.Core.Monitoring;

public interface INotifier
{
    Task NotifyAsync(BackupJob job, BackupRun run, CancellationToken cancellationToken);
}
