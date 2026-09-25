using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Scheduling;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Engine;

/// <summary>
/// Executes one backup run: source → archive (compression) → encryption → checksum → upload (with retry,
/// resume and verification) → retention → history → notifications.
/// </summary>
public sealed class BackupJobRunner(
    RunRepository runs,
    SettingsRepository settings,
    ISourceFactory sourceFactory,
    IDestinationFactory destinationFactory,
    IEnumerable<INotifier> notifiers,
    ILogger<BackupJobRunner> logger,
    HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? SharedHttp.Client;

    public async Task<BackupRun> RunAsync(BackupJob job, RunTrigger trigger, CancellationToken cancellationToken)
    {
        var run = new BackupRun { JobId = job.Id, JobName = job.Name, Trigger = trigger, StartedAt = DateTimeOffset.UtcNow };
        runs.Insert(run);

        var log = new RunLog(logger, job.Name);
        await PingAsync(job, HealthCheckSignal.Start, null, log);
        var staging = Path.Combine(GetStagingRoot(settings.Get()), run.Id.ToString("N"));
        SourceSnapshot? snapshot = null;

        try
        {
            Validate(job);
            Directory.CreateDirectory(staging);
            log.Info($"Backup started ({trigger}). Source: {job.Source.Kind}.");

            // 1. Source (database dumps are retried, e.g. when the server is busy).
            var source = sourceFactory.Create(job.Source);
            snapshot = await RetryExecutor.ExecuteAsync(
                job.Retry, "Source", (_, ct) => source.PrepareAsync(new SourceContext(staging, log), ct), log, cancellationToken);

            if (snapshot.Entries.Count == 0)
            {
                throw new InvalidOperationException("Nothing to back up: the source produced no files.");
            }

            // 2. Compression.
            var zipPath = Path.Combine(staging, BackupNaming.CreateFileName(job.FilePrefix, run.StartedAt, encrypted: false));
            var archive = await ArchiveBuilder.CreateAsync(snapshot.Entries, zipPath, job.Processing.Compression, log.Warn, cancellationToken);
            log.Info($"Archive created: {archive.EntryCount} file(s), {FormatSize(archive.SizeBytes)}{(archive.SkippedCount > 0 ? $", {archive.SkippedCount} skipped" : string.Empty)}.");

            if (job.Processing.VerifyArchive)
            {
                await ArchiveBuilder.VerifyAsync(zipPath, archive.EntryCount, cancellationToken);
                log.Info("Archive verified.");
            }

            // 3. Encryption.
            var finalPath = zipPath;
            if (job.Processing.Encrypt)
            {
                finalPath = zipPath + AesFileEncryptor.FileExtension;
                await AesFileEncryptor.EncryptAsync(zipPath, finalPath, job.Processing.EncryptionPassword!, cancellationToken);
                File.Delete(zipPath);
                log.Info("Archive encrypted (AES-256).");

                if (job.Processing.VerifyArchive)
                {
                    await AesFileEncryptor.VerifyAsync(finalPath, job.Processing.EncryptionPassword!, cancellationToken);
                    log.Info("Encrypted archive verified.");
                }
            }

            // 4. Checksum.
            var fileName = Path.GetFileName(finalPath);
            var hash = await Checksum.Sha256Async(finalPath, cancellationToken);
            var sidecar = await Checksum.WriteSidecarAsync(finalPath, hash, cancellationToken);
            run.FileName = fileName;
            run.SizeBytes = new FileInfo(finalPath).Length;
            run.Sha256 = hash;
            log.Info($"SHA-256: {hash}");

            // 5. Destinations.
            var destinations = job.Destinations.Where(d => d.Enabled).ToList();
            var failures = new List<string>();
            foreach (var destination in destinations)
            {
                try
                {
                    await UploadAsync(job, destination, finalPath, sidecar, log, cancellationToken);
                    await ApplyRetentionAsync(job, destination, fileName, log, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{destination.Name}: {ex.Message}");
                    log.Error($"Destination '{destination.Name}' failed.", ex);
                }
            }

            var succeeded = destinations.Count - failures.Count;
            run.Status = failures.Count == 0 ? RunStatus.Succeeded : succeeded > 0 ? RunStatus.PartiallySucceeded : RunStatus.Failed;
            run.Message = failures.Count == 0
                ? $"Backup stored on {succeeded} destination(s)."
                : $"{failures.Count} of {destinations.Count} destination(s) failed. {string.Join(" | ", failures)}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Status = RunStatus.Cancelled;
            run.Message = "The backup was cancelled.";
            log.Warn(run.Message);
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.Message = ex.Message;
            log.Error("Backup failed.", ex);
        }
        finally
        {
            Cleanup(snapshot, staging, log);
            run.FinishedAt = DateTimeOffset.UtcNow;
            log.Info($"Finished with status {run.Status} in {run.Duration:hh\\:mm\\:ss}.");
            run.Log = log.ToString();
            runs.Complete(run);
        }

        await PingAsync(job, run.Status == RunStatus.Succeeded ? HealthCheckSignal.Success : HealthCheckSignal.Failure, run.Message, log: null);

        var wanted = run.Status == RunStatus.Succeeded ? job.Notifications.OnSuccess : job.Notifications.OnFailure;
        if (wanted)
        {
            await NotifyAsync(notifiers, Notification.ForRun(job, run), logger);
        }

        return run;
    }

    /// <summary>Delivers a notification through every notifier; failures are logged, never thrown.</summary>
    public static async Task NotifyAsync(IEnumerable<INotifier> notifiers, Notification notification, ILogger logger)
    {
        foreach (var notifier in notifiers)
        {
            try
            {
                await notifier.NotifyAsync(notification, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError("Notifier {Notifier} failed: {Error}", notifier.GetType().Name, ex.Message);
            }
        }
    }

    private async Task PingAsync(BackupJob job, HealthCheckSignal signal, string? message, RunLog? log)
    {
        if (string.IsNullOrWhiteSpace(job.Notifications.HealthCheckUrl))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await HealthCheckPinger.PingAsync(_http, job.Notifications.HealthCheckUrl, signal, message, timeout.Token);
        }
        catch (Exception ex)
        {
            // The URL may contain a secret token: only log the error.
            log?.Warn($"Health-check ping ({signal}) failed: {ex.Message}");
            logger.LogWarning("Health-check ping ({Signal}) for job {Job} failed: {Error}", signal, job.Name, ex.Message);
        }
    }

    public static string GetStagingRoot(AppSettings appSettings) =>
        string.IsNullOrWhiteSpace(appSettings.StagingDirectory) ? StorixPaths.DefaultStagingDirectory : appSettings.StagingDirectory;

    /// <summary>Returns a list of configuration problems, empty when the job can run.</summary>
    public static IReadOnlyList<string> GetValidationErrors(BackupJob job)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(job.Name))
        {
            errors.Add("Name is required.");
        }

        if (ScheduleCalculator.Validate(job.Schedule) is { } scheduleError)
        {
            errors.Add($"Schedule: {scheduleError}");
        }

        if (job.Processing.Encrypt && string.IsNullOrEmpty(job.Processing.EncryptionPassword))
        {
            errors.Add("Encryption is enabled but no password is set.");
        }

        if (!job.Destinations.Any(d => d.Enabled))
        {
            errors.Add("At least one enabled destination is required.");
        }

        switch (job.Source.Kind)
        {
            case SourceKind.Files when job.Source.Files.Paths.Count == 0:
                errors.Add("Select at least one file or folder.");
                break;
            case SourceKind.SqlServer when job.Source.SqlServer.Databases.Count == 0:
                errors.Add("Select at least one SQL Server database.");
                break;
            case SourceKind.MongoDb when string.IsNullOrWhiteSpace(job.Source.MongoDb.ConnectionString):
                errors.Add("MongoDB connection string is required.");
                break;
        }

        return errors;
    }

    private static void Validate(BackupJob job)
    {
        var errors = GetValidationErrors(job);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid job configuration: " + string.Join(" ", errors));
        }
    }

    private async Task UploadAsync(BackupJob job, DestinationDefinition definition, string archivePath, string sidecarPath, RunLog log, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(archivePath);
        var size = new FileInfo(archivePath).Length;
        log.Info($"Uploading to '{definition.Name}' ({definition.Kind}).");

        await RetryExecutor.ExecuteAsync(job.Retry, $"Upload to '{definition.Name}'", async (_, ct) =>
        {
            // A fresh connection per attempt; partial uploads left by a failed attempt are resumed.
            await using var destination = destinationFactory.Create(definition);
            var watch = Stopwatch.StartNew();

            await destination.UploadAsync(archivePath, fileName, progress: null, ct);
            await destination.UploadAsync(sidecarPath, Path.GetFileName(sidecarPath), progress: null, ct);

            // Verification: the remote file must exist with the exact size.
            var remote = (await destination.ListAsync(ct)).FirstOrDefault(f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase));
            if (remote is null || remote.Size != size)
            {
                throw new IOException($"Upload verification failed: remote size {remote?.Size.ToString() ?? "missing"}, expected {size}.");
            }

            var seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.001);
            log.Info($"Uploaded to '{definition.Name}' and verified ({FormatSize(size)} at {FormatSize((long)(size / seconds))}/s).");
        }, log, cancellationToken);
    }

    private async Task ApplyRetentionAsync(BackupJob job, DestinationDefinition definition, string currentFile, RunLog log, CancellationToken cancellationToken)
    {
        await using var destination = destinationFactory.Create(definition);
        try
        {
            var files = await destination.ListAsync(cancellationToken);
            var prefix = job.FilePrefix + "_";

            // Partial upload cleanup: leftovers of interrupted runs of this job.
            foreach (var partial in files.Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                                     && f.Name.EndsWith(BackupNaming.PartialSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                await destination.DeleteAsync(partial.Name, cancellationToken);
                log.Info($"Removed stale partial upload '{partial.Name}' from '{definition.Name}'.");
            }

            var backups = BackupNaming.ParseBackups(job.FilePrefix, files.Select(f => f.Name));

            foreach (var backup in RetentionPlanner.SelectForDeletion(backups, job.Retention, DateTimeOffset.UtcNow))
            {
                if (string.Equals(backup.Name, currentFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var file in backup.Files)
                {
                    await destination.DeleteAsync(file, cancellationToken);
                }

                log.Info($"Retention: deleted '{backup.Name}' from '{definition.Name}'.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A retention problem must not turn a successful backup into a failure.
            log.Warn($"Retention on '{definition.Name}' failed: {ex.Message}");
        }
    }

    private static void Cleanup(SourceSnapshot? snapshot, string staging, RunLog log)
    {
        try
        {
            foreach (var entry in snapshot?.Entries.Where(e => e.DeleteAfterRun) ?? [])
            {
                if (File.Exists(entry.SourcePath))
                {
                    File.Delete(entry.SourcePath);
                }
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Cleanup of temporary files failed: {ex.Message}");
        }
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
