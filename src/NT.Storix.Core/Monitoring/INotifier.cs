using NT.Storix.Core.Models;

namespace NT.Storix.Core.Monitoring;

public enum NotificationEvent
{
    Success,
    Failure,
    /// <summary>Dead man's switch: no successful backup for too long.</summary>
    Stale,
    Test,
    Summary,
}

/// <summary>A message about a job, delivered by every configured notifier.</summary>
public sealed record Notification(NotificationEvent Event, string Title, string Text, BackupJob? Job = null, BackupRun? Run = null)
{
    public bool IsProblem => Event is NotificationEvent.Failure or NotificationEvent.Stale;

    public static Notification ForRun(BackupJob job, BackupRun run)
    {
        var success = run.Status == RunStatus.Succeeded;
        var text = new System.Text.StringBuilder()
            .AppendLine($"Job: {run.JobName}")
            .AppendLine($"Status: {run.Status}")
            .AppendLine($"Machine: {Environment.MachineName}")
            .AppendLine($"Started: {run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Duration: {run.Duration:hh\\:mm\\:ss}");
        if (run.FileName is not null)
        {
            text.AppendLine($"File: {run.FileName} ({run.SizeBytes:N0} bytes)");
        }

        if (!string.IsNullOrWhiteSpace(run.Message))
        {
            text.AppendLine().AppendLine(run.Message);
        }

        return new Notification(
            success ? NotificationEvent.Success : NotificationEvent.Failure,
            $"{(success ? "✅" : "❌")} {job.Name}: {run.Status}",
            text.ToString().TrimEnd(),
            job,
            run);
    }

    public static Notification ForStale(BackupJob job, DateTimeOffset? lastSuccess) => new(
        NotificationEvent.Stale,
        $"⚠️ {job.Name}: no successful backup",
        lastSuccess is null
            ? $"Job '{job.Name}' on {Environment.MachineName} has never completed successfully."
            : $"Job '{job.Name}' on {Environment.MachineName} has not completed successfully since {lastSuccess.Value.ToLocalTime():yyyy-MM-dd HH:mm} " +
              $"({(DateTimeOffset.UtcNow - lastSuccess.Value).TotalHours:0} hours ago).",
        job);
}

public interface INotifier
{
    Task NotifyAsync(Notification notification, CancellationToken cancellationToken);
}
