using System.Security.Cryptography;
using NT.Storix.Core.Dedup;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Engine;

public sealed partial class BackupJobRunner
{
    /// <summary>
    /// Deduplicated backup: chunk every file once, then per destination upload only the chunks it lacks (in pack
    /// files), upload the snapshot last, apply retention and delete packs no snapshot references any more.
    /// </summary>
    private async Task DedupBackupAsync(BackupJob job, BackupRun run, SourceSnapshot source, string staging, RunLog log, CancellationToken cancellationToken)
    {
        var secret = job.Processing.Encrypt ? EncryptionSecret.Resolve(job.Processing) : null;
        var keysForSalt = DedupEngine.KeyCache(secret);
        var keys = secret is null ? DedupKeys.Plain : keysForSalt(GetDedupSalt(job))!;

        var files = await Task.Run(() => DedupEngine.Chunk(source.Entries, keys, log.Warn, cancellationToken), cancellationToken);
        var total = files.Sum(f => f.Size);
        var distinct = files.SelectMany(f => f.Chunks).Select(c => c.Id).Distinct().Count();
        log.Info($"Chunked {files.Count} file(s), {FormatSize(total)} into {distinct} distinct chunk(s).");

        var name = BackupNaming.CreateFileName(job.FilePrefix, run.StartedAt, encrypted: false)[..^".zip".Length] + DedupSnapshot.Extension;
        var destinations = job.Destinations.Where(d => d.Enabled).ToList();
        var failures = new List<string>();
        long uploaded = 0;
        foreach (var definition in destinations)
        {
            if (circuitBreaker?.OpenUntil(definition.Id) is { } openUntil)
            {
                failures.Add($"{definition.Name}: skipped: failed repeatedly, next attempt after {openUntil.ToLocalTime():HH:mm}");
                continue;
            }

            var work = Path.Combine(staging, "dedup-" + definition.Id.ToString("N"));
            try
            {
                RepositoryState state;
                await using (var destination = destinationFactory.Create(definition))
                {
                    var names = (await destination.ListAsync(cancellationToken)).Select(f => f.Name).ToList();
                    var snapshots = BackupNaming.ParseBackups(job.FilePrefix, names).Where(b => DedupEngine.IsSnapshot(b.Name)).Select(b => b.Name).ToList();
                    var cache = Path.Combine(GetStagingRoot(settings.Get()), "dedup-cache", definition.Id.ToString("N"));
                    state = await DedupEngine.LoadAsync(destination, snapshots, cache, keys, keysForSalt, strict: false, cancellationToken);
                }

                var built = await DedupEngine.BuildAsync(files, state.Known, keys, job.FilePrefix, work, cancellationToken);
                built.Snapshot.CreatedAt = run.StartedAt;
                var snapshotPath = Path.Combine(work, name);
                await built.Snapshot.WriteAsync(snapshotPath, keys, cancellationToken);
                log.Info($"'{definition.Name}': {built.NewChunks} new chunk(s) ({FormatSize(built.NewBytes)} in {built.Packs.Count} pack(s)), {built.ReusedChunks} reused.");

                // Packs first (skipped when already there after an interrupted attempt), the snapshot last.
                var items = built.Packs.Select(p => new UploadItem(p.Path, p.Name, p.Size, SkipIfPresent: true)).ToList();
                items.Add(new UploadItem(snapshotPath, name, new FileInfo(snapshotPath).Length, SkipIfPresent: false));
                await UploadAsync(job, definition, items, log, cancellationToken);
                circuitBreaker?.RecordSuccess(definition.Id);
                uploaded = Math.Max(uploaded, items.Sum(i => i.ExpectedSize ?? 0));

                await ApplyRetentionAsync(job, definition, name, log, cancellationToken);
                await CollectGarbageAsync(job, definition, name, keys, keysForSalt, log, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                circuitBreaker?.RecordFailure(definition.Id);
                failures.Add($"{definition.Name}: {ex.Message}");
                log.Error($"Destination '{definition.Name}' failed.", ex);
            }
            finally
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
        }

        var succeeded = destinations.Count - failures.Count;
        if (succeeded > 0 && sqlBackups is not null)
        {
            foreach (var info in source.SqlBackups)
            {
                info.JobId = job.Id;
                info.RunId = run.Id;
                info.ArchiveName = name;
                sqlBackups.Add(info);
            }
        }

        run.FileName = name;
        run.SizeBytes = uploaded;
        run.Status = failures.Count == 0 ? RunStatus.Succeeded : succeeded > 0 ? RunStatus.PartiallySucceeded : RunStatus.Failed;
        run.Message = failures.Count == 0
            ? $"Backup stored on {succeeded} destination(s) ({FormatSize(total)} of data, {FormatSize(uploaded)} uploaded)."
            : $"{failures.Count} of {destinations.Count} destination(s) failed. {string.Join(" | ", failures)}";
    }

    /// <summary>Deletes packs that no remaining snapshot references (after retention, or left by failed runs).</summary>
    private async Task CollectGarbageAsync(BackupJob job, DestinationDefinition definition, string current, DedupKeys keys, Func<byte[], DedupKeys?> keysForSalt, RunLog log, CancellationToken cancellationToken)
    {
        try
        {
            await using var destination = destinationFactory.Create(definition);
            var names = (await destination.ListAsync(cancellationToken)).Select(f => f.Name).ToList();
            var snapshots = BackupNaming.ParseBackups(job.FilePrefix, names).Where(b => DedupEngine.IsSnapshot(b.Name)).Select(b => b.Name).ToList();
            if (!snapshots.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                return; // Never collect without the snapshot that was just uploaded.
            }

            var cache = Path.Combine(GetStagingRoot(settings.Get()), "dedup-cache", definition.Id.ToString("N"));
            var state = await DedupEngine.LoadAsync(destination, snapshots, cache, keys, keysForSalt, strict: true, cancellationToken);
            var unused = names.Where(n => DedupEngine.IsPack(job.FilePrefix, n) && !state.ReferencedPacks.Contains(n)).ToList();
            foreach (var pack in unused)
            {
                await destination.DeleteAsync(pack, cancellationToken);
            }

            if (unused.Count > 0)
            {
                log.Info($"Removed {unused.Count} unused pack(s) from '{definition.Name}'.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn($"Cleaning up unused packs on '{definition.Name}' failed: {ex.Message}");
        }
    }

    /// <summary>Random per-job salt of the chunk keys, kept in the local database.</summary>
    private byte[] GetDedupSalt(BackupJob job)
    {
        var key = $"dedup-salt:{job.Id:N}";
        if (settings.GetValue(key) is { } stored)
        {
            return Convert.FromBase64String(stored);
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        settings.SetValue(key, Convert.ToBase64String(salt));
        return salt;
    }
}
