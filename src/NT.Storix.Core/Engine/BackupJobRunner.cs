using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Scheduling;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Engine;

/// <summary>
/// Executes one backup run: source → archive (compression) → encryption → checksum → upload (with retry,
/// resume and verification) → retention → history → notifications.
/// </summary>
public sealed partial class BackupJobRunner(
    RunRepository runs,
    SettingsRepository settings,
    ISourceFactory sourceFactory,
    IDestinationFactory destinationFactory,
    IEnumerable<INotifier> notifiers,
    ILogger<BackupJobRunner> logger,
    HttpClient? http = null,
    SqlBackupRepository? sqlBackups = null,
    CircuitBreaker? circuitBreaker = null,
    JobRepository? jobs = null)
{
    private readonly HttpClient _http = http ?? SharedHttp.Client;

    public async Task<BackupRun> RunAsync(BackupJob job, RunTrigger trigger, CancellationToken cancellationToken)
    {
        var run = new BackupRun { JobId = job.Id, JobName = job.Name, Trigger = trigger, StartedAt = DateTimeOffset.UtcNow };
        runs.Insert(run);
        using var activity = StorixTelemetry.Source.StartActivity("backup");
        activity?.SetTag("storix.job", job.Name);
        activity?.SetTag("storix.job_id", job.Id.ToString());
        activity?.SetTag("storix.trigger", trigger.ToString());

        var log = new RunLog(logger, job.Name);
        await PingAsync(job, HealthCheckSignal.Start, null, log);
        var staging = Path.Combine(GetStagingRoot(settings.Get()), run.Id.ToString("N"));
        SourceSnapshot? snapshot = null;

        try
        {
            Validate(job);
            Directory.CreateDirectory(staging);
            log.Info($"Backup started ({trigger}). Source: {job.Source.Kind}.");

            if (job.Source.Kind == SourceKind.CopyOf)
            {
                await CopyBackupsAsync(job, run, staging, log, cancellationToken);
            }
            else
            {
                await BackupAsync(job, run, staging, log, s => snapshot = s, cancellationToken);
            }
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
            if (!string.IsNullOrWhiteSpace(job.Hooks.PostCommand))
            {
                try
                {
                    var post = await RunHookAsync(job, run, "Post-command", job.Hooks.PostCommand, log, CancellationToken.None);
                    if (!post.Succeeded)
                    {
                        log.Warn(post.TimedOut ? "The post-command timed out." : $"The post-command failed with exit code {post.ExitCode}.");
                    }
                }
                catch (Exception ex)
                {
                    log.Warn($"The post-command could not be started: {ex.Message}");
                }
            }

            Cleanup(snapshot, staging, log);
            run.FinishedAt = DateTimeOffset.UtcNow;
            log.Info($"Finished with status {run.Status} in {run.Duration:hh\\:mm\\:ss}.");
            run.Log = log.ToString();
            runs.Complete(run);
            activity?.SetTag("storix.status", run.Status.ToString());
            activity?.SetTag("storix.size_bytes", run.SizeBytes);
            activity?.SetStatus(run.Status == RunStatus.Succeeded ? System.Diagnostics.ActivityStatusCode.Ok : System.Diagnostics.ActivityStatusCode.Error, run.Message);
        }

        await PingAsync(job, run.Status == RunStatus.Succeeded ? HealthCheckSignal.Success : HealthCheckSignal.Failure, run.Message, log: null);

        var wanted = run.Status == RunStatus.Succeeded ? job.Notifications.OnSuccess : job.Notifications.OnFailure;
        if (wanted)
        {
            await NotifyAsync(notifiers, Notification.ForRun(job, run), logger);
        }

        return run;
    }

    /// <summary>The regular pipeline: hooks, source, archive, encryption, checksum, upload and retention.</summary>
    private async Task BackupAsync(BackupJob job, BackupRun run, string staging, RunLog log, Action<SourceSnapshot> onSnapshot, CancellationToken cancellationToken)
    {

        // 0. Pre-command (e.g. stop an application so its files are consistent).
        if (!string.IsNullOrWhiteSpace(job.Hooks.PreCommand))
        {
            var pre = await RunHookAsync(job, run, "Pre-command", job.Hooks.PreCommand, log, cancellationToken);
            if (!pre.Succeeded && job.Hooks.AbortOnPreCommandFailure)
            {
                throw new InvalidOperationException(pre.TimedOut ? "The pre-command timed out." : $"The pre-command failed with exit code {pre.ExitCode}.");
            }
        }

        // 1. Source (database dumps are retried, e.g. when the server is busy).
        var source = sourceFactory.Create(job.Source);
        var snapshot = await RetryExecutor.ExecuteAsync(
            job.Retry, "Source", (_, ct) => source.PrepareAsync(new SourceContext(staging, log), ct), log, cancellationToken);
        onSnapshot(snapshot);

        if (snapshot.Entries.Count == 0)
        {
            throw new InvalidOperationException("Nothing to back up: the source produced no files.");
        }

        // Fail early when the staging disk is obviously too small.
        var previousSize = runs.GetRecent(job.Id, 10).FirstOrDefault(r => r.Status == RunStatus.Succeeded && r.SizeBytes > 0)?.SizeBytes;
        FreeSpace.Ensure(staging, FreeSpace.EstimateStagingBytes(snapshot.Entries, previousSize, job.Processing.Encrypt), "the staging folder");

        await CheckpointAsync(job, log, uploading: false, cancellationToken);

        // 2. Compression.
        var zipPath = Path.Combine(staging, BackupNaming.CreateFileName(job.FilePrefix, run.StartedAt, encrypted: false, ArchiveBuilder.UsesZstd(job.Processing.Compression)));
        var archive = await ArchiveBuilder.CreateAsync(snapshot.Entries, zipPath, job.Processing.Compression, log.Warn, cancellationToken);
        log.Info($"Archive created: {archive.EntryCount} file(s), {FormatSize(archive.SizeBytes)}{(archive.SkippedCount > 0 ? $", {archive.SkippedCount} skipped" : string.Empty)}.");

        if (job.Processing.VerifyArchive)
        {
            await ArchiveBuilder.VerifyAsync(zipPath, archive.EntryCount, cancellationToken);
            log.Info("Archive verified.");
        }

        await CheckpointAsync(job, log, uploading: false, cancellationToken);

        // 3. Encryption.
        var finalPath = zipPath;
        if (job.Processing.Encrypt)
        {
            finalPath = zipPath + AesFileEncryptor.FileExtension;
            var secret = EncryptionSecret.Resolve(job.Processing)!;
            await AesFileEncryptor.EncryptAsync(zipPath, finalPath, secret, cancellationToken);
            File.Delete(zipPath);
            var publicKey = job.Processing.EncryptionMode == EncryptionMode.PublicKey;
            log.Info(publicKey ? "Archive encrypted (AES-256, key wrapped with the job's RSA public key)." : "Archive encrypted (AES-256).");

            // With a public key the private key is (deliberately) not on this machine: nothing to verify against.
            if (job.Processing.VerifyArchive && !publicKey)
            {
                await AesFileEncryptor.VerifyAsync(finalPath, secret, cancellationToken);
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

        // File index for browsing and single-file restores (encrypted like the archive).
        var indexPath = finalPath + BackupIndex.Extension;
        await new BackupIndex { Archive = fileName, Entries = [.. archive.Entries] }
            .WriteAsync(indexPath, job.Processing.Encrypt ? EncryptionSecret.Resolve(job.Processing) : null, cancellationToken);
        var indexItem = new UploadItem(indexPath, Path.GetFileName(indexPath), new FileInfo(indexPath).Length, SkipIfPresent: false);

        // Files to upload, in order. Split backups upload their manifest last (it marks a complete set).
        var uploads = new List<UploadItem>();
        if (job.Processing.SplitSizeMb > 0 && run.SizeBytes > job.Processing.SplitSizeMb * 1024L * 1024L)
        {
            var manifest = await ChunkedArchive.SplitAsync(finalPath, job.Processing.SplitSizeMb * 1024L * 1024L, hash, cancellationToken);
            File.Delete(finalPath);
            var manifestPath = finalPath + ChunkManifest.Extension;
            await File.WriteAllTextAsync(manifestPath, manifest.ToJson(), cancellationToken);
            log.Info($"Split into {manifest.Chunks.Count} volume(s) of up to {job.Processing.SplitSizeMb} MB.");

            uploads.AddRange(manifest.Chunks.Select(c => new UploadItem(Path.Combine(staging, c.Name), c.Name, c.Size, SkipIfPresent: true)));
            uploads.Add(new UploadItem(sidecar, Path.GetFileName(sidecar), null, SkipIfPresent: false));
            uploads.Add(indexItem);
            uploads.Add(new UploadItem(manifestPath, Path.GetFileName(manifestPath), new FileInfo(manifestPath).Length, SkipIfPresent: false));
        }
        else
        {
            uploads.Add(new UploadItem(finalPath, fileName, run.SizeBytes.Value, SkipIfPresent: false));
            uploads.Add(new UploadItem(sidecar, Path.GetFileName(sidecar), null, SkipIfPresent: false));
            uploads.Add(indexItem);
        }

        // Wait for the allowed upload window (e.g. only at night).
        if (UploadWindow.Parse(job.Schedule.UploadWindow) is { } window)
        {
            var zone = string.IsNullOrWhiteSpace(job.Schedule.TimeZoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(job.Schedule.TimeZoneId);
            var wait = window.Delay(DateTimeOffset.UtcNow, zone);
            if (wait > TimeSpan.Zero)
            {
                log.Info($"Outside the upload window {window}; waiting {wait:hh\\:mm} before uploading.");
                await Task.Delay(wait, cancellationToken);
            }
        }

        // 5. Destinations.
        var destinations = job.Destinations.Where(d => d.Enabled).ToList();
        var failures = new List<string>();
        foreach (var destination in destinations)
        {
            if (circuitBreaker?.OpenUntil(destination.Id) is { } openUntil)
            {
                var message = $"skipped: failed repeatedly, next attempt after {openUntil.ToLocalTime():HH:mm}";
                failures.Add($"{destination.Name}: {message}");
                log.Warn($"Destination '{destination.Name}' {message}.");
                continue;
            }

            try
            {
                await UploadAsync(job, destination, uploads, log, cancellationToken);
                circuitBreaker?.RecordSuccess(destination.Id);
                await ApplyRetentionAsync(job, destination, fileName, log, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                circuitBreaker?.RecordFailure(destination.Id);
                failures.Add($"{destination.Name}: {ex.Message}");
                log.Error($"Destination '{destination.Name}' failed.", ex);
            }
        }

        var succeeded = destinations.Count - failures.Count;
        if (succeeded > 0 && sqlBackups is not null)
        {
            foreach (var info in snapshot.SqlBackups)
            {
                info.JobId = job.Id;
                info.RunId = run.Id;
                info.ArchiveName = fileName;
                sqlBackups.Add(info);
            }
        }
        run.Status = failures.Count == 0 ? RunStatus.Succeeded : succeeded > 0 ? RunStatus.PartiallySucceeded : RunStatus.Failed;
        run.Message = failures.Count == 0
            ? $"Backup stored on {succeeded} destination(s)."
            : $"{failures.Count} of {destinations.Count} destination(s) failed. {string.Join(" | ", failures)}";
    }

    /// <summary>Poll interval while a run is paused or waiting for an unmetered connection.</summary>
    internal static TimeSpan PausePollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Waits while the job is paused from the UI, or (for uploads) while the connection is metered and the
    /// settings ask to hold uploads.
    /// </summary>
    private async Task CheckpointAsync(BackupJob job, RunLog log, bool uploading, CancellationToken cancellationToken)
    {
        var announced = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? reason = null;
            if (runs.IsPaused(job.Id))
            {
                reason = "paused from the manager";
            }
            else if (uploading && settings.Get().PauseOnMeteredConnection && MeteredConnection.IsMetered())
            {
                reason = "waiting for an unmetered connection";
            }

            if (reason is null)
            {
                if (announced)
                {
                    log.Info("Resumed.");
                }

                return;
            }

            if (!announced)
            {
                log.Info($"Backup {reason}.");
                announced = true;
            }

            await Task.Delay(PausePollInterval, cancellationToken);
        }
    }

    private static async Task<HookResult> RunHookAsync(BackupJob job, BackupRun run, string name, string command, RunLog log, CancellationToken cancellationToken)
    {
        log.Info($"{name} started.");
        var environment = new Dictionary<string, string?>
        {
            ["STORIX_JOB_ID"] = job.Id.ToString(),
            ["STORIX_JOB_NAME"] = job.Name,
            ["STORIX_RUN_ID"] = run.Id.ToString(),
            ["STORIX_TRIGGER"] = run.Trigger.ToString(),
            ["STORIX_STATUS"] = run.Status.ToString(),
            ["STORIX_FILE"] = run.FileName,
            ["STORIX_MESSAGE"] = run.Message,
        };

        var result = await HookRunner.RunAsync(command, environment, TimeSpan.FromSeconds(Math.Max(1, job.Hooks.TimeoutSeconds)), cancellationToken);
        if (result.Output.Length > 0)
        {
            log.Info($"{name} output:{Environment.NewLine}{result.Output}");
        }

        log.Info($"{name} finished with exit code {result.ExitCode}{(result.TimedOut ? " (timed out)" : string.Empty)}.");
        return result;
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

        if (job.Processing.Encrypt && job.Processing.EncryptionMode == EncryptionMode.Password
            && string.IsNullOrEmpty(job.Processing.EncryptionPassword) && string.IsNullOrWhiteSpace(job.Processing.EncryptionKeyFile))
        {
            errors.Add("Encryption is enabled but no password or key file is set.");
        }

        if (job.Processing.Encrypt && job.Processing.EncryptionMode == EncryptionMode.PublicKey)
        {
            try
            {
                PrivateKeySecret.Fingerprint(job.Processing.PublicKeyPem ?? string.Empty);
            }
            catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                errors.Add("Public-key encryption needs a valid RSA public key (PEM).");
            }
        }

        try
        {
            UploadWindow.Parse(job.Schedule.UploadWindow);
        }
        catch (FormatException ex)
        {
            errors.Add(ex.Message);
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
            case SourceKind.CopyOf when string.IsNullOrWhiteSpace(job.Source.CopyOf.Job):
                errors.Add("Choose the job whose backups are copied.");
                break;
            case SourceKind.Sqlite when string.IsNullOrWhiteSpace(job.Source.Sqlite.DatabasePaths):
                errors.Add("Select at least one SQLite database.");
                break;
            case SourceKind.PostgreSql when string.IsNullOrWhiteSpace(job.Source.PostgreSql.Host):
            case SourceKind.MySql when string.IsNullOrWhiteSpace(job.Source.MySql.Host):
            case SourceKind.Redis when string.IsNullOrWhiteSpace(job.Source.Redis.Host):
                errors.Add("The database host is required.");
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

    /// <param name="ExpectedSize">Size verified on the destination after the upload (null = not verified).</param>
    /// <param name="SkipIfPresent">Skip when the destination already has the file with the expected size (chunk-level resume).</param>
    private sealed record UploadItem(string LocalPath, string RemoteName, long? ExpectedSize, bool SkipIfPresent);

    private async Task UploadAsync(BackupJob job, DestinationDefinition definition, IReadOnlyList<UploadItem> items, RunLog log, CancellationToken cancellationToken)
    {
        var total = items.Sum(i => i.ExpectedSize ?? 0);
        log.Info($"Uploading to '{definition.Name}' ({definition.Kind}).");

        using var activity = StorixTelemetry.Source.StartActivity("upload");
        activity?.SetTag("storix.destination", definition.Name);
        activity?.SetTag("storix.destination_kind", definition.Kind.ToString());
        await RetryExecutor.ExecuteAsync(job.Retry, $"Upload to '{definition.Name}'", async (_, ct) =>
        {
            // A fresh connection per attempt; partial uploads left by a failed attempt are resumed.
            await using var destination = destinationFactory.Create(definition);
            var watch = Stopwatch.StartNew();

            var remote = items.Any(i => i.SkipIfPresent)
                ? (await destination.ListAsync(ct)).ToDictionary(f => f.Name, f => f.Size, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var skipped = 0;
            foreach (var item in items)
            {
                if (item.SkipIfPresent && remote.TryGetValue(item.RemoteName, out var existing) && existing == item.ExpectedSize)
                {
                    skipped++;
                    continue;
                }

                await CheckpointAsync(job, log, uploading: true, ct);

                await destination.UploadAsync(item.LocalPath, item.RemoteName, progress: null, ct);
            }

            // Verification: every file must exist remotely with the exact size.
            var listing = (await destination.ListAsync(ct)).ToDictionary(f => f.Name, f => f.Size, StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.Where(i => i.ExpectedSize is not null))
            {
                if (!listing.TryGetValue(item.RemoteName, out var size) || size != item.ExpectedSize)
                {
                    throw new IOException($"Upload verification failed for '{item.RemoteName}': remote size {(listing.ContainsKey(item.RemoteName) ? size.ToString() : "missing")}, expected {item.ExpectedSize}.");
                }
            }

            var seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.001);
            log.Info($"Uploaded to '{definition.Name}' and verified ({FormatSize(total)} at {FormatSize((long)(total / seconds))}/s" +
                     (skipped > 0 ? $", {skipped} volume(s) already present" : string.Empty) + ").");
        }, log, cancellationToken);
    }

    /// <param name="copiedPrefix">For copy jobs: file prefix of the job whose backups were copied.</param>
    private async Task ApplyRetentionAsync(BackupJob job, DestinationDefinition definition, string currentFile, RunLog log, CancellationToken cancellationToken, string? copiedPrefix = null)
    {
        await using var destination = destinationFactory.Create(definition);
        var filePrefix = copiedPrefix ?? job.FilePrefix;
        try
        {
            var files = await destination.ListAsync(cancellationToken);
            var prefix = filePrefix + "_";

            // Partial upload cleanup: leftovers of interrupted runs of this job.
            foreach (var partial in files.Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                                     && f.Name.EndsWith(BackupNaming.PartialSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                await destination.DeleteAsync(partial.Name, cancellationToken);
                log.Info($"Removed stale partial upload '{partial.Name}' from '{definition.Name}'.");
            }

            // Volumes of interrupted split uploads (no manifest) from earlier runs.
            foreach (var orphan in BackupNaming.OrphanedFiles(filePrefix, files.Select(f => f.Name), keep: currentFile).ToList())
            {
                await destination.DeleteAsync(orphan, cancellationToken);
                log.Info($"Removed incomplete volume '{orphan}' from '{definition.Name}'.");
            }

            var backups = BackupNaming.ParseBackups(filePrefix, files.Select(f => f.Name));

            foreach (var backup in RetentionPlanner.SelectForDeletion(backups, job.Retention, DateTimeOffset.UtcNow))
            {
                if (string.Equals(backup.Name, currentFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    foreach (var file in backup.Files)
                    {
                        await destination.DeleteAsync(file, cancellationToken);
                    }
                }
                catch (BackupLockedException locked)
                {
                    log.Info($"Retention: kept '{backup.Name}' on '{definition.Name}': {locked.Message}");
                    continue;
                }

                if (copiedPrefix is null)
                {
                    sqlBackups?.DeleteByArchive(job.Id, [backup.Name]);
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
        foreach (var resource in snapshot?.Resources ?? [])
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception ex)
            {
                log.Warn($"Releasing {resource.GetType().Name} failed: {ex.Message}");
            }
        }

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

    public static string FormatSize(long bytes)
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
