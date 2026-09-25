using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;

namespace NT.Storix.Core.Tests;

public class StatusSummaryTests
{
    private static BackupJob Job(string name, bool enabled = true) => new() { Name = name, Enabled = enabled };

    [Fact]
    public void Failed_job_makes_the_status_an_error()
    {
        var ok = Job("ok");
        var bad = Job("bad");
        var runs = new Dictionary<Guid, BackupRun>
        {
            [ok.Id] = new() { Status = RunStatus.Succeeded, FinishedAt = DateTimeOffset.UtcNow },
            [bad.Id] = new() { Status = RunStatus.Failed },
        };

        var summary = StatusSummary.Create([ok, bad], id => runs.GetValueOrDefault(id));

        Assert.Equal(OverallStatus.Error, summary.Status);
        Assert.Equal(["bad"], summary.Failed);
        Assert.Contains("bad", summary.Text);
    }

    [Fact]
    public void Running_and_disabled_jobs_are_reported()
    {
        var running = Job("nightly");
        var disabled = Job("old", enabled: false);
        var runs = new Dictionary<Guid, BackupRun>
        {
            [running.Id] = new() { Status = RunStatus.Running },
            [disabled.Id] = new() { Status = RunStatus.Failed },
        };

        var summary = StatusSummary.Create([running, disabled], id => runs.GetValueOrDefault(id));

        Assert.Equal(OverallStatus.Running, summary.Status);
        Assert.Equal(1, summary.Jobs);
        Assert.Empty(summary.Failed);
    }

    [Fact]
    public void Text_is_truncated_to_the_tooltip_limit()
    {
        var jobs = Enumerable.Range(0, 30).Select(i => Job($"a-rather-long-job-name-{i}")).ToList();

        var summary = StatusSummary.Create(jobs, _ => new BackupRun { Status = RunStatus.Failed });

        Assert.True(summary.Text.Length <= StatusSummary.MaxTextLength);
        Assert.EndsWith("...", summary.Text);
    }

    [Fact]
    public void No_enabled_jobs_is_a_warning()
    {
        Assert.Equal(OverallStatus.Warning, StatusSummary.Create([], _ => null).Status);
    }
}
