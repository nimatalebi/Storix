using System.Globalization;
using System.Text;
using NT.KeepstoneBackup.Core.Models;

namespace NT.KeepstoneBackup.Core.Monitoring;

/// <summary>Renders KeepstoneBackup state in the Prometheus text exposition format.</summary>
public static class PrometheusExporter
{
    public static string Render(IReadOnlyList<BackupJob> jobs, IReadOnlyList<BackupRun> runs, IReadOnlyCollection<Guid> running)
    {
        var text = new StringBuilder();
        void Header(string name, string type, string help) => text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
                                                                  .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        void Sample(string name, string labels, double value) =>
            text.Append(name).Append('{').Append(labels).Append("} ").Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');

        Header("keepstonebackup_up", "gauge", "The KeepstoneBackup service is running.");
        text.Append("keepstonebackup_up 1\n");

        Header("keepstonebackup_job_enabled", "gauge", "1 when the job runs on its schedule.");
        foreach (var job in jobs)
        {
            Sample("keepstonebackup_job_enabled", Labels(job), job.Enabled ? 1 : 0);
        }

        Header("keepstonebackup_job_running", "gauge", "1 while the job is running.");
        foreach (var job in jobs)
        {
            Sample("keepstonebackup_job_running", Labels(job), running.Contains(job.Id) ? 1 : 0);
        }

        Header("keepstonebackup_job_last_success_timestamp_seconds", "gauge", "Unix time of the last successful backup.");
        Header("keepstonebackup_job_last_size_bytes", "gauge", "Size of the last successful backup.");
        Header("keepstonebackup_job_last_duration_seconds", "gauge", "Duration of the last finished backup.");
        Header("keepstonebackup_job_last_status", "gauge", "Last backup status (1 for the current status label).");
        foreach (var job in jobs)
        {
            var backups = runs.Where(r => r.JobId == job.Id && r.Trigger != RunTrigger.RestoreDrill).ToList();
            var success = backups.Where(r => r.Status == RunStatus.Succeeded).MaxBy(r => r.StartedAt);
            var last = backups.Where(r => r.Status != RunStatus.Running).MaxBy(r => r.StartedAt);
            if (success is not null)
            {
                Sample("keepstonebackup_job_last_success_timestamp_seconds", Labels(job), success.StartedAt.ToUnixTimeSeconds());
                Sample("keepstonebackup_job_last_size_bytes", Labels(job), success.SizeBytes ?? 0);
            }

            if (last is not null)
            {
                Sample("keepstonebackup_job_last_duration_seconds", Labels(job), last.Duration?.TotalSeconds ?? 0);
                Sample("keepstonebackup_job_last_status", $"{Labels(job)},status=\"{last.Status}\"", 1);
            }
        }

        Header("keepstonebackup_runs_total", "counter", "Recorded runs by job, kind and status.");
        foreach (var group in runs.GroupBy(r => (r.JobId, r.JobName, Kind: r.Trigger == RunTrigger.RestoreDrill ? "drill" : "backup", r.Status)))
        {
            Sample("keepstonebackup_runs_total", $"job=\"{Escape(group.Key.JobName)}\",job_id=\"{group.Key.JobId}\",kind=\"{group.Key.Kind}\",status=\"{group.Key.Status}\"", group.Count());
        }

        return text.ToString();
    }

    private static string Labels(BackupJob job) => $"job=\"{Escape(job.Name)}\",job_id=\"{job.Id}\"";

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
