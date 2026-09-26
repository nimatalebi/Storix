using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Engine;

public sealed partial class BackupJobRunner
{
    /// <summary>
    /// Copy job: backups of another job are copied as they are (still encrypted, no password needed) from one
    /// of its destinations to this job's destinations. Only backups this job's retention policy keeps are
    /// copied; each is checked against its SHA-256 before upload. Retention then runs on the copies.
    /// </summary>
    private async Task CopyBackupsAsync(BackupJob job, BackupRun run, string staging, RunLog log, CancellationToken cancellationToken)
    {
        var (sourceJob, sourceDefinition) = ResolveCopySource(job);
        log.Info($"Copying backups of '{sourceJob.Name}' from '{sourceDefinition.Name}'.");

        await using var source = destinationFactory.Create(sourceDefinition);
        var sourceFiles = await source.ListAsync(cancellationToken);
        var sizes = sourceFiles.ToDictionary(f => f.Name, f => f.Size, StringComparer.OrdinalIgnoreCase);
        var backups = BackupNaming.ParseBackups(sourceJob.FilePrefix, sizes.Keys);
        if (backups.Count == 0)
        {
            throw new InvalidOperationException($"'{sourceDefinition.Name}' has no backups of '{sourceJob.Name}' yet.");
        }

        if (backups.Any(b => Dedup.DedupEngine.IsSnapshot(b.Name)))
        {
            throw new NotSupportedException($"'{sourceJob.Name}' uses deduplicated backups, which copy jobs do not support yet. Add the other destination to that job instead.");
        }

        var expired = RetentionPlanner.SelectForDeletion(backups, job.Retention, DateTimeOffset.UtcNow).ToHashSet();
        var wanted = backups.Where(b => !expired.Contains(b)).OrderBy(b => b.CreatedAt).ToList();
        var newest = wanted[^1].Name;

        var destinations = job.Destinations.Where(d => d.Enabled).ToList();
        var failures = new List<string>();
        var copied = 0;
        long bytes = 0;
        foreach (var definition in destinations)
        {
            if (circuitBreaker?.OpenUntil(definition.Id) is { } openUntil)
            {
                failures.Add($"{definition.Name}: skipped: failed repeatedly, next attempt after {openUntil.ToLocalTime():HH:mm}");
                continue;
            }

            try
            {
                Dictionary<string, long> present;
                await using (var target = destinationFactory.Create(definition))
                {
                    present = (await target.ListAsync(cancellationToken)).ToDictionary(f => f.Name, f => f.Size, StringComparer.OrdinalIgnoreCase);
                }

                var missing = wanted.Where(b => b.Files.Any(f => !present.TryGetValue(f, out var size) || size != sizes[f])).ToList();
                log.Info($"'{definition.Name}': {wanted.Count - missing.Count} of {wanted.Count} backup(s) already present.");
                foreach (var backup in missing)
                {
                    var items = await DownloadForCopyAsync(source, backup, sizes, staging, log, cancellationToken);
                    try
                    {
                        await UploadAsync(job, definition, items, log, cancellationToken);
                    }
                    finally
                    {
                        foreach (var item in items)
                        {
                            File.Delete(item.LocalPath);
                        }
                    }

                    copied++;
                    bytes += items.Sum(i => i.ExpectedSize ?? 0);
                }

                circuitBreaker?.RecordSuccess(definition.Id);
                await ApplyRetentionAsync(job, definition, newest, log, cancellationToken, copiedPrefix: sourceJob.FilePrefix);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                circuitBreaker?.RecordFailure(definition.Id);
                failures.Add($"{definition.Name}: {ex.Message}");
                log.Error($"Destination '{definition.Name}' failed.", ex);
            }
        }

        var succeeded = destinations.Count - failures.Count;
        run.FileName = newest;
        run.SizeBytes = bytes;
        run.Status = failures.Count == 0 ? RunStatus.Succeeded : succeeded > 0 ? RunStatus.PartiallySucceeded : RunStatus.Failed;
        run.Message = failures.Count == 0
            ? $"{copied} backup cop{(copied == 1 ? "y" : "ies")} made; {wanted.Count} backup(s) of '{sourceJob.Name}' on {succeeded} destination(s)."
            : $"{failures.Count} of {destinations.Count} destination(s) failed. {string.Join(" | ", failures)}";
    }

    private (BackupJob Job, DestinationDefinition Destination) ResolveCopySource(BackupJob job)
    {
        if (jobs is null)
        {
            throw new InvalidOperationException("Copy jobs need access to the job list.");
        }

        var options = job.Source.CopyOf;
        var all = jobs.GetAll();
        var sourceJob = (Guid.TryParse(options.Job, out var id)
                            ? all.FirstOrDefault(j => j.Id == id)
                            : all.FirstOrDefault(j => string.Equals(j.Name, options.Job?.Trim(), StringComparison.OrdinalIgnoreCase)))
                        ?? throw new InvalidOperationException($"The job '{options.Job}' to copy from does not exist.");
        if (sourceJob.Id == job.Id || sourceJob.Source.Kind == SourceKind.CopyOf)
        {
            throw new InvalidOperationException("A copy job must copy from a regular backup job.");
        }

        var destination = string.IsNullOrWhiteSpace(options.FromDestination)
            ? sourceJob.Destinations.FirstOrDefault(d => d.Enabled)
            : sourceJob.Destinations.FirstOrDefault(d => string.Equals(d.Name, options.FromDestination.Trim(), StringComparison.OrdinalIgnoreCase));
        if (destination is not null && !DestinationCapabilities.CanRestore(destination.Kind))
        {
            throw new InvalidOperationException($"'{destination.Name}' is archive-only (Telegram): copy jobs need a destination Storix can read from.");
        }

        return (sourceJob, destination ?? throw new InvalidOperationException($"'{sourceJob.Name}' has no destination '{options.FromDestination}'."));
    }

    /// <summary>Downloads every file of a backup and checks it against the checksum sidecar or the volume manifest.</summary>
    private static async Task<List<UploadItem>> DownloadForCopyAsync(
        IBackupDestination source, BackupFileInfo backup, IReadOnlyDictionary<string, long> sizes, string staging, RunLog log, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(staging, "copy");
        Directory.CreateDirectory(folder);
        var items = new List<UploadItem>();
        foreach (var name in backup.Files)
        {
            var local = Path.Combine(folder, name);
            await source.DownloadAsync(name, local, null, cancellationToken);
            items.Add(new UploadItem(local, name, new FileInfo(local).Length, SkipIfPresent: false));
        }

        // Verify before spreading the copy: a corrupted source must not overwrite good copies elsewhere.
        var sidecar = items.FirstOrDefault(i => i.RemoteName.EndsWith(Checksum.SidecarExtension, StringComparison.OrdinalIgnoreCase));
        var archive = items.FirstOrDefault(i => string.Equals(i.RemoteName, backup.Name, StringComparison.OrdinalIgnoreCase));
        if (sidecar is not null && archive is not null)
        {
            var expected = (await File.ReadAllTextAsync(sidecar.LocalPath, cancellationToken)).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var actual = await Checksum.Sha256Async(archive.LocalPath, cancellationToken);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"'{backup.Name}' on the source does not match its checksum; not copied.");
            }
        }

        if (items.FirstOrDefault(i => i.RemoteName.EndsWith(ChunkManifest.Extension, StringComparison.OrdinalIgnoreCase)) is { } manifestItem)
        {
            var manifest = ChunkManifest.Parse(await File.ReadAllTextAsync(manifestItem.LocalPath, cancellationToken));
            foreach (var chunk in manifest.Chunks)
            {
                var path = Path.Combine(folder, chunk.Name);
                if (!File.Exists(path) || !string.Equals(await Checksum.Sha256Async(path, cancellationToken), chunk.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Volume '{chunk.Name}' of '{backup.Name}' is missing or corrupted on the source; not copied.");
                }
            }

            // The manifest marks a complete set: upload it last.
            items.Remove(manifestItem);
            items.Add(manifestItem);
        }

        log.Info($"Downloaded and verified '{backup.Name}' ({FormatSize(items.Sum(i => i.ExpectedSize ?? 0))}).");
        return items;
    }
}
