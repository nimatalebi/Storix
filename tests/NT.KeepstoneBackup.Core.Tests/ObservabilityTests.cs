using System.Diagnostics;
using NT.KeepstoneBackup.Core.Models;
using NT.KeepstoneBackup.Core.Monitoring;

namespace NT.KeepstoneBackup.Core.Tests;

public class ObservabilityTests
{
    [Fact]
    public void Prometheus_output_has_job_gauges_and_run_counters()
    {
        var job = new BackupJob { Name = "Web \"prod\"" };
        var started = DateTimeOffset.FromUnixTimeSeconds(1_780_000_000);
        var runs = new List<BackupRun>
        {
            new() { JobId = job.Id, JobName = job.Name, Status = RunStatus.Succeeded, StartedAt = started, FinishedAt = started.AddSeconds(42), SizeBytes = 1234 },
            new() { JobId = job.Id, JobName = job.Name, Status = RunStatus.Failed, StartedAt = started.AddDays(1), FinishedAt = started.AddDays(1).AddSeconds(5) },
            new() { JobId = job.Id, JobName = job.Name, Status = RunStatus.Succeeded, StartedAt = started.AddDays(2), Trigger = RunTrigger.RestoreDrill },
        };

        var text = PrometheusExporter.Render([job], runs, [job.Id]);

        var labels = $"job=\"Web \\\"prod\\\"\",job_id=\"{job.Id}\"";
        Assert.Contains("keepstonebackup_up 1", text);
        Assert.Contains($"keepstonebackup_job_running{{{labels}}} 1", text);
        Assert.Contains($"keepstonebackup_job_last_success_timestamp_seconds{{{labels}}} 1780000000", text);
        Assert.Contains($"keepstonebackup_job_last_size_bytes{{{labels}}} 1234", text);
        Assert.Contains($"keepstonebackup_job_last_status{{{labels},status=\"Failed\"}} 1", text);
        Assert.Contains("kind=\"drill\",status=\"Succeeded\"} 1", text);
        Assert.Contains("# TYPE keepstonebackup_runs_total counter", text);
    }

    [Fact]
    public async Task Backup_runs_emit_trace_activities()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == KeepstoneBackupTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (activities) { activities.Add(activity); } },
        };
        ActivitySource.AddActivityListener(listener);

        using var temp = new TempDirectory();
        temp.WriteFile("source/a.txt", "a");
        var (_, run) = await RestoreTests.BackupAsync(temp, encrypt: false);

        lock (activities)
        {
            // Other tests back up jobs with the same name in parallel: match this run's job id.
            var backup = activities.Single(a => a.OperationName == "backup" && (string?)a.GetTagItem("keepstonebackup.job_id") == run.JobId.ToString());
            Assert.Equal("Succeeded", backup.GetTagItem("keepstonebackup.status"));
            Assert.Contains(activities, a => a.OperationName == "upload" && a.Parent?.Id == backup.Id);
        }

        Assert.Equal(RunStatus.Succeeded, run.Status);
    }
}
