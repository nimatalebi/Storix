using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;

namespace NT.Storix.Core.Tests;

public class SummaryTests
{
    [Fact]
    public void Summary_counts_runs_failures_and_drills_per_job()
    {
        var now = new DateTimeOffset(2026, 6, 8, 8, 0, 0, TimeSpan.Zero);
        var sql = new BackupJob { Name = "SQL" };
        var files = new BackupJob { Name = "Files" };
        BackupRun Run(BackupJob job, RunStatus status, int daysAgo, long size = 1024, RunTrigger trigger = RunTrigger.Schedule) =>
            new() { JobId = job.Id, JobName = job.Name, Status = status, StartedAt = now.AddDays(-daysAgo), SizeBytes = size, Trigger = trigger };

        var runs = new List<BackupRun>
        {
            Run(sql, RunStatus.Succeeded, 1, 1024 * 1024),
            Run(sql, RunStatus.Failed, 2),
            Run(files, RunStatus.Succeeded, 3),
            Run(files, RunStatus.Succeeded, 30), // Outside the period.
            Run(sql, RunStatus.Succeeded, 1, 0, RunTrigger.RestoreDrill),
        };

        var text = SummaryReport.Build([sql, files], runs, now.AddDays(-7), now);

        Assert.Contains("Backups: 3, succeeded: 2, partial: 0, failed: 1", text);
        Assert.Contains("Restore drills: 1, passed: 1", text);
        Assert.Contains("SQL  ⚠", text);
        Assert.Contains("latest size: 1 MB", text);
    }

    [Fact]
    public void Summary_is_due_once_per_week_at_the_configured_time()
    {
        var settings = new WeeklySummarySettings { Enabled = true, Day = DayOfWeek.Monday, Hour = 8 };
        var monday9 = new DateTimeOffset(2026, 6, 8, 9, 0, 0, TimeSpan.Zero);

        Assert.True(SummaryReport.IsDue(settings, monday9, null, TimeZoneInfo.Utc));
        Assert.False(SummaryReport.IsDue(settings, monday9, monday9.AddHours(-1), TimeZoneInfo.Utc));
        Assert.False(SummaryReport.IsDue(settings, monday9.AddHours(-2), null, TimeZoneInfo.Utc)); // 07:00
        Assert.False(SummaryReport.IsDue(settings, monday9.AddDays(1), null, TimeZoneInfo.Utc));   // Tuesday
        Assert.False(SummaryReport.IsDue(new WeeklySummarySettings(), monday9, null, TimeZoneInfo.Utc));
    }
}
