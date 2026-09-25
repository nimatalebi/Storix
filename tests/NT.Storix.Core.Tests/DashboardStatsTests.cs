using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;

namespace NT.Storix.Core.Tests;

public class DashboardStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    private static BackupRun Run(BackupJob job, int daysAgo, RunStatus status, long? size = null) => new()
    {
        JobId = job.Id,
        JobName = job.Name,
        Status = status,
        StartedAt = Now.AddDays(-daysAgo),
        FinishedAt = Now.AddDays(-daysAgo).AddMinutes(5),
        SizeBytes = size,
    };

    [Fact]
    public void Success_rate_counts_only_the_period_and_ignores_drills()
    {
        var job = new BackupJob { Name = "db" };
        var runs = new List<BackupRun>
        {
            Run(job, 1, RunStatus.Succeeded, 100),
            Run(job, 2, RunStatus.PartiallySucceeded, 100),
            Run(job, 3, RunStatus.Failed),
            Run(job, 60, RunStatus.Failed),
            new() { JobId = job.Id, Trigger = RunTrigger.RestoreDrill, Status = RunStatus.Failed, StartedAt = Now },
        };

        var stats = DashboardStats.Compute([job], runs, Now).Jobs.Single();

        Assert.Equal(3, stats.Runs);
        Assert.Equal(2, stats.Succeeded);
        Assert.Equal(1, stats.Failed);
        Assert.Equal(2.0 / 3, stats.SuccessRate!.Value, 6);
    }

    [Fact]
    public void Stored_space_follows_the_retention_policy_and_is_grouped_by_destination()
    {
        var job = new BackupJob
        {
            Name = "files",
            Retention = new RetentionPolicy { KeepLast = 3 },
            Destinations = [new DestinationDefinition { Name = "NAS" }, new DestinationDefinition { Name = "S3" }],
        };
        var runs = Enumerable.Range(1, 5).Select(i => Run(job, i, RunStatus.Succeeded, 100)).ToList();

        var dashboard = DashboardStats.Compute([job], runs, Now);

        Assert.Equal(300, dashboard.Jobs.Single().EstimatedStored);
        Assert.Equal(2, dashboard.Destinations.Count);
        Assert.All(dashboard.Destinations, d => Assert.Equal(300, d.EstimatedStored));
        Assert.Equal(600, dashboard.TotalStored);
    }

    [Fact]
    public void Growth_trend_and_forecast_are_linear()
    {
        var job = new BackupJob { Name = "growing" };

        // 1000 bytes on day -10, growing by 100 bytes per day.
        var runs = Enumerable.Range(0, 11).Select(i => Run(job, 10 - i, RunStatus.Succeeded, 1000 + (i * 100))).ToList();

        var stats = DashboardStats.Compute([job], runs, Now, forecastDays: 30).Jobs.Single();

        Assert.Equal(100, stats.GrowthPerDay!.Value, 3);
        Assert.Equal(2000, stats.LatestSize);
        Assert.Equal(5000, stats.ForecastSize);
    }

    [Fact]
    public void Single_backup_has_no_trend()
    {
        var job = new BackupJob { Name = "once" };

        var stats = DashboardStats.Compute([job], [Run(job, 1, RunStatus.Succeeded, 42)], Now).Jobs.Single();

        Assert.Null(stats.GrowthPerDay);
        Assert.Equal(42, stats.ForecastSize);
    }
}
