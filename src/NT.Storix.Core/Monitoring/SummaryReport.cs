using System.Text;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Monitoring;

/// <summary>Weekly overview: per job number of runs, failures, last success and data volume.</summary>
public static class SummaryReport
{
    public static string Build(IReadOnlyList<BackupJob> jobs, IReadOnlyList<BackupRun> runs, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var period = runs.Where(r => r.StartedAt >= fromUtc && r.StartedAt < toUtc).ToList();
        var text = new StringBuilder()
            .AppendLine($"Storix summary for {Environment.MachineName}")
            .AppendLine($"{fromUtc.ToLocalTime():yyyy-MM-dd} to {toUtc.ToLocalTime():yyyy-MM-dd}")
            .AppendLine();

        var backups = period.Where(r => r.Trigger != RunTrigger.RestoreDrill).ToList();
        var failed = backups.Count(r => r.Status is RunStatus.Failed or RunStatus.Interrupted);
        text.AppendLine($"Backups: {backups.Count}, succeeded: {backups.Count(r => r.Status == RunStatus.Succeeded)}, " +
                        $"partial: {backups.Count(r => r.Status == RunStatus.PartiallySucceeded)}, failed: {failed}");
        text.AppendLine($"Data stored: {BackupJobRunner.FormatSize(backups.Where(r => r.Status != RunStatus.Failed).Sum(r => r.SizeBytes ?? 0))}");
        var drills = period.Where(r => r.Trigger == RunTrigger.RestoreDrill).ToList();
        if (drills.Count > 0)
        {
            text.AppendLine($"Restore drills: {drills.Count}, passed: {drills.Count(r => r.Status == RunStatus.Succeeded)}");
        }

        text.AppendLine();
        foreach (var job in jobs.OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase))
        {
            var jobRuns = backups.Where(r => r.JobId == job.Id).ToList();
            var lastSuccess = runs.Where(r => r.JobId == job.Id && r.Status == RunStatus.Succeeded && r.Trigger != RunTrigger.RestoreDrill)
                                  .OrderByDescending(r => r.StartedAt).FirstOrDefault();
            var flag = !job.Enabled ? "  (disabled)" : jobRuns.Any(r => r.Status is RunStatus.Failed or RunStatus.Interrupted) ? "  ⚠" : string.Empty;
            text.AppendLine($"{job.Name}{flag}");
            text.AppendLine($"  runs: {jobRuns.Count}, failed: {jobRuns.Count(r => r.Status is RunStatus.Failed or RunStatus.Interrupted)}, " +
                            $"last success: {(lastSuccess is null ? "never" : lastSuccess.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}, " +
                            $"latest size: {(lastSuccess?.SizeBytes is { } size ? BackupJobRunner.FormatSize(size) : "-")}");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>True when the summary should be sent now (configured weekday and hour, not sent in the last 6 days).</summary>
    public static bool IsDue(WeeklySummarySettings settings, DateTimeOffset nowUtc, DateTimeOffset? lastSentUtc, TimeZoneInfo zone)
    {
        if (!settings.Enabled)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        return local.DayOfWeek == settings.Day && local.Hour >= settings.Hour && (lastSentUtc is null || nowUtc - lastSentUtc.Value > TimeSpan.FromDays(6));
    }
}
