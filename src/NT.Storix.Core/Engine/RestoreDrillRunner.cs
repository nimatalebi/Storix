using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Engine;

/// <summary>
/// Test restore of the latest backup: download, verify, decrypt and extract into a temporary folder, then
/// (optionally) restore SQL Server databases into a temporary database with DBCC CHECKDB, or dry-run mongorestore.
/// The result is recorded in the history as a run with the <see cref="RunTrigger.RestoreDrill"/> trigger.
/// </summary>
public sealed class RestoreDrillRunner(
    RunRepository runs,
    SettingsRepository settings,
    IDestinationFactory destinationFactory,
    IEnumerable<INotifier> notifiers,
    ILogger<RestoreDrillRunner> logger)
{
    public async Task<BackupRun> RunAsync(BackupJob job, CancellationToken cancellationToken)
    {
        var run = new BackupRun { JobId = job.Id, JobName = job.Name, Trigger = RunTrigger.RestoreDrill, StartedAt = DateTimeOffset.UtcNow };
        runs.Insert(run);
        var log = new RunLog(logger, job.Name);
        string? folder = null;

        try
        {
            var destination = job.Destinations.FirstOrDefault(d => d.Enabled) ?? throw new InvalidOperationException("The job has no enabled destination.");
            var restore = new RestoreService(destinationFactory);
            log.Info($"Restore drill started (destination '{destination.Name}').");

            var latest = (await restore.ListBackupsAsync(job, destination, cancellationToken)).FirstOrDefault()
                         ?? throw new InvalidOperationException($"No backup of this job was found on '{destination.Name}'.");
            run.FileName = latest.Name;
            log.Info($"Latest backup: {latest.Name} ({latest.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}).");

            folder = DrillFolder(job, run);
            var secret = job.Processing.Encrypt ? EncryptionSecret.Resolve(job.Processing) : null;
            var result = await restore.RestoreFromDestinationAsync(destination, latest.Name, new RestoreRequest(folder, secret), new Progress<string>(log.Info), cancellationToken);
            if (result.Files.Count == 0)
            {
                throw new InvalidDataException("The backup is empty.");
            }

            run.SizeBytes = result.TotalBytes;
            log.Info($"Extracted {result.Files.Count} file(s), {BackupJobRunner.FormatSize(result.TotalBytes)}{(result.ChecksumVerified ? ", checksum verified" : string.Empty)}.");

            if (job.Source.Kind == SourceKind.SqlServer && job.RestoreDrill.CheckSqlDatabases)
            {
                foreach (var bak in Directory.EnumerateFiles(folder, "*.bak", SearchOption.AllDirectories))
                {
                    await CheckSqlBackupAsync(job.Source.SqlServer, bak, log, cancellationToken);
                }
            }
            else if (job.Source.Kind == SourceKind.MongoDb && job.RestoreDrill.CheckMongoArchive)
            {
                foreach (var archive in Directory.EnumerateFiles(folder, "*.archive", SearchOption.AllDirectories))
                {
                    await MongoDbRestorer.DryRunAsync(MongorestorePath(job.Source.MongoDb), job.Source.MongoDb.ConnectionString!, archive, cancellationToken);
                    log.Info($"mongorestore --dryRun succeeded for {Path.GetFileName(archive)}.");
                }
            }

            run.Status = RunStatus.Succeeded;
            run.Message = $"Restore drill passed: {result.Files.Count} file(s) restored from {latest.Name}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Status = RunStatus.Cancelled;
            run.Message = "The restore drill was cancelled.";
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.Message = "Restore drill failed: " + ex.Message;
            log.Error("Restore drill failed.", ex);
        }
        finally
        {
            if (folder is not null)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"Could not delete the drill folder: {ex.Message}");
                }
            }

            run.FinishedAt = DateTimeOffset.UtcNow;
            log.Info($"Restore drill finished with status {run.Status}.");
            run.Log = log.ToString();
            runs.Complete(run);
        }

        var wanted = run.Status == RunStatus.Succeeded ? job.Notifications.OnSuccess : job.Notifications.OnFailure;
        if (wanted)
        {
            var notification = Notification.ForRun(job, run) with { Title = $"{(run.Status == RunStatus.Succeeded ? "✅" : "❌")} {job.Name}: restore drill {run.Status}" };
            await BackupJobRunner.NotifyAsync(notifiers, notification, logger);
        }

        return run;
    }

    /// <summary>
    /// SQL Server must read the .bak file itself, so drills of SQL jobs with a backup directory extract there.
    /// </summary>
    private string DrillFolder(BackupJob job, BackupRun run)
    {
        var root = job.Source.Kind == SourceKind.SqlServer && !string.IsNullOrWhiteSpace(job.Source.SqlServer.BackupDirectory)
            ? job.Source.SqlServer.BackupDirectory
            : BackupJobRunner.GetStagingRoot(settings.Get());
        return Path.Combine(root, $"storix-drill-{run.Id:N}");
    }

    private static async Task CheckSqlBackupAsync(SqlServerSourceOptions options, string bak, RunLog log, CancellationToken cancellationToken)
    {
        var database = $"storix_drill_{Guid.NewGuid():N}"[..28];
        log.Info($"Restoring {Path.GetFileName(bak)} into temporary database {database}.");
        try
        {
            await SqlServerRestorer.RestoreAsync(options.ConnectionString!, bak, database, null, replace: false, cancellationToken, options.CommandTimeoutSeconds);

            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var check = connection.CreateCommand();
            check.CommandTimeout = options.CommandTimeoutSeconds;
            check.CommandText = $"DBCC CHECKDB ({SqlServerRestorer.Quote(database)}) WITH NO_INFOMSGS, ALL_ERRORMSGS";
            await check.ExecuteNonQueryAsync(cancellationToken);
            log.Info($"DBCC CHECKDB passed for {Path.GetFileName(bak)}.");
        }
        finally
        {
            try
            {
                await using var connection = new SqlConnection(options.ConnectionString);
                await connection.OpenAsync(CancellationToken.None);
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE {SqlServerRestorer.Quote(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {SqlServerRestorer.Quote(database)}; END";
                drop.Parameters.AddWithValue("@name", database);
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (SqlException ex)
            {
                log.Warn($"Could not drop the temporary database {database}: {ex.Message}");
            }
        }
    }

    private static string? MongorestorePath(MongoDbSourceOptions options) =>
        string.IsNullOrWhiteSpace(options.MongodumpPath)
            ? null
            : Path.Combine(Path.GetDirectoryName(options.MongodumpPath) ?? string.Empty, OperatingSystem.IsWindows() ? "mongorestore.exe" : "mongorestore");
}
