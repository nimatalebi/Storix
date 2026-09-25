using NT.Storix.Core.Models;

namespace NT.Storix.Core.Monitoring;

public enum OverallStatus
{
    Ok,
    Running,
    Warning,
    Error,
}

/// <summary>One-line status of all jobs, used by the tray icon.</summary>
public sealed record StatusSummary(OverallStatus Status, int Jobs, IReadOnlyList<string> Running, IReadOnlyList<string> Failed, DateTimeOffset? LastSuccess)
{
    /// <summary>Tray tooltips are limited to 127 characters.</summary>
    public const int MaxTextLength = 127;

    public static StatusSummary Create(IEnumerable<BackupJob> jobs, Func<Guid, BackupRun?> lastRun)
    {
        var count = 0;
        var running = new List<string>();
        var failed = new List<string>();
        DateTimeOffset? lastSuccess = null;
        foreach (var job in jobs.Where(j => j.Enabled))
        {
            count++;
            var last = lastRun(job.Id);
            switch (last?.Status)
            {
                case RunStatus.Running:
                    running.Add(job.Name);
                    break;
                case RunStatus.Failed or RunStatus.Interrupted:
                    failed.Add(job.Name);
                    break;
                case RunStatus.Succeeded or RunStatus.PartiallySucceeded:
                    var finished = last.FinishedAt ?? last.StartedAt;
                    if (lastSuccess is null || finished > lastSuccess)
                    {
                        lastSuccess = finished;
                    }

                    break;
            }
        }

        var status = failed.Count > 0 ? OverallStatus.Error
            : running.Count > 0 ? OverallStatus.Running
            : count == 0 ? OverallStatus.Warning
            : OverallStatus.Ok;
        return new StatusSummary(status, count, running, failed, lastSuccess);
    }

    public string Text
    {
        get
        {
            var text = Status switch
            {
                OverallStatus.Error => $"Storix: {Failed.Count} job(s) failed ({string.Join(", ", Failed)})",
                OverallStatus.Running => $"Storix: running {string.Join(", ", Running)}",
                OverallStatus.Warning => "Storix: no enabled jobs",
                _ => $"Storix: {Jobs} job(s) OK" + (LastSuccess is { } at ? $", last backup {at.ToLocalTime():g}" : string.Empty),
            };
            return text.Length <= MaxTextLength ? text : text[..(MaxTextLength - 3)] + "...";
        }
    }
}
