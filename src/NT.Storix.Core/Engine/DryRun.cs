using System.Text;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.Core.Scheduling;

namespace NT.Storix.Core.Engine;

/// <summary>Shows what a job would do without writing or uploading anything.</summary>
public static class DryRun
{
    public static async Task<string> RunAsync(BackupJob job, IDestinationFactory destinations, CancellationToken cancellationToken)
    {
        var report = new StringBuilder();
        report.AppendLine($"Dry run of '{job.Name}' ({DateTime.Now:yyyy-MM-dd HH:mm})");
        report.AppendLine();

        var errors = BackupJobRunner.GetValidationErrors(job);
        report.AppendLine(errors.Count == 0 ? "Configuration: OK" : "Configuration problems:");
        foreach (var error in errors)
        {
            report.AppendLine($"  - {error}");
        }

        report.AppendLine($"Schedule: {ScheduleCalculator.Describe(job.Schedule)}");
        try
        {
            if (ScheduleCalculator.GetNextOccurrence(job.Schedule, DateTimeOffset.UtcNow) is { } next)
            {
                report.AppendLine($"Next run: {next.ToLocalTime():yyyy-MM-dd HH:mm}");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"Next run: invalid schedule ({ex.Message})");
        }

        report.AppendLine();
        report.AppendLine($"Source: {job.Source.Kind}");
        switch (job.Source.Kind)
        {
            case SourceKind.Files:
                var entries = await new Sources.FileSource(job.Source.Files)
                    .PrepareAsync(new Sources.SourceContext(Path.GetTempPath(), new RunLog(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, job.Name)), cancellationToken);
                var size = entries.Entries.Sum(e => new FileInfo(e.SourcePath).Length);
                report.AppendLine($"  {entries.Entries.Count:N0} file(s), {BackupJobRunner.FormatSize(size)} before compression");
                foreach (var entry in entries.Entries.Take(20))
                {
                    report.AppendLine($"    {entry.EntryName}");
                }

                if (entries.Entries.Count > 20)
                {
                    report.AppendLine($"    ... and {entries.Entries.Count - 20:N0} more");
                }

                break;
            case SourceKind.SqlServer:
                report.AppendLine($"  {job.Source.SqlServer.BackupType} backup of: {string.Join(", ", job.Source.SqlServer.Databases)}");
                report.AppendLine($"  Connection: {await TestSqlAsync(job.Source.SqlServer.ConnectionString, cancellationToken)}");
                break;
            default:
                report.AppendLine($"  {job.Source.Kind} dump (not executed in a dry run)");
                break;
        }

        report.AppendLine();
        report.AppendLine($"Processing: compression {job.Processing.Compression}, encryption {(job.Processing.Encrypt ? "AES-256" : "off")}" +
                          (job.Processing.SplitSizeMb > 0 ? $", volumes of {job.Processing.SplitSizeMb} MB" : string.Empty));
        report.AppendLine();
        report.AppendLine("Destinations:");
        foreach (var destination in job.Destinations)
        {
            if (!destination.Enabled)
            {
                report.AppendLine($"  {destination} - disabled");
                continue;
            }

            try
            {
                await using var target = destinations.Create(destination);
                await target.TestAsync(cancellationToken);
                var backups = Processing.BackupNaming.ParseBackups(job.FilePrefix, (await target.ListAsync(cancellationToken)).Select(f => f.Name));
                report.AppendLine($"  {destination} - reachable, {backups.Count} existing backup(s)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.AppendLine($"  {destination} - NOT reachable: {ex.Message}");
            }
        }

        report.AppendLine();
        report.AppendLine("Nothing was written or uploaded.");
        return report.ToString();
    }

    private static async Task<string> TestSqlAsync(string? connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return $"OK ({connection.DataSource}, SQL Server {connection.ServerVersion})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "FAILED: " + ex.Message;
        }
    }
}
