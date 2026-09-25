using System.Security.Cryptography;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Dedup;

public sealed record ChunkPlan(string Id, long Offset, int Size);

public sealed record FilePlan(ArchiveEntry Entry, long Size, DateTimeOffset Modified, string Sha256, IReadOnlyList<ChunkPlan> Chunks);

/// <summary>What a destination already stores: chunks (usable when their keys match) and every referenced pack.</summary>
public sealed record RepositoryState(Dictionary<string, DedupChunk> Known, HashSet<string> ReferencedPacks, IReadOnlyList<string> Snapshots);

public sealed record BuiltSnapshot(DedupSnapshot Snapshot, IReadOnlyList<(string Path, string Name, long Size)> Packs, long NewBytes, int NewChunks, int ReusedChunks);

/// <summary>
/// Deduplicated backups ("incremental forever"): files are split into content-defined chunks, and only chunks a
/// destination does not have yet are uploaded, grouped into pack files (<c>{prefix}_pack_{id}.pack</c>). Each backup
/// is a small snapshot file (<c>{prefix}_{timestamp}.snap</c>) that lists its files and where their chunks are.
/// </summary>
public static class DedupEngine
{
    public const long PackTargetSize = 32L * 1024 * 1024;
    public const string PackExtension = ".pack";

    public static string NewPackName(string prefix) => $"{prefix}_pack_{Guid.NewGuid():N}{PackExtension}";

    public static bool IsPack(string prefix, string name) =>
        name.StartsWith(prefix + "_pack_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(PackExtension, StringComparison.OrdinalIgnoreCase);

    public static bool IsSnapshot(string name) => name.EndsWith(DedupSnapshot.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns cached keys per salt: the password is stretched once per salt, not once per file.</summary>
    public static Func<byte[], DedupKeys?> KeyCache(string? secret)
    {
        var cache = new Dictionary<string, DedupKeys>(StringComparer.Ordinal);
        return salt =>
        {
            if (secret is null)
            {
                return null;
            }

            var key = Convert.ToBase64String(salt);
            if (!cache.TryGetValue(key, out var keys))
            {
                cache[key] = keys = DedupKeys.Derive(secret, salt);
            }

            return keys;
        };
    }

    /// <summary>Reads every file once: SHA-256 and content-defined chunk ids.</summary>
    public static List<FilePlan> Chunk(IEnumerable<ArchiveEntry> entries, DedupKeys keys, Action<string>? warn, CancellationToken cancellationToken)
    {
        var cdc = new FastCdc();
        var result = new List<FilePlan>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream input;
            try
            {
                input = new FileStream(entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024);
            }
            catch (Exception ex) when (entry.Optional && ex is IOException or UnauthorizedAccessException)
            {
                warn?.Invoke($"Skipped '{entry.SourcePath}': {ex.Message}");
                continue;
            }

            using (input)
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var modified = File.GetLastWriteTimeUtc(entry.SourcePath);
                var chunks = new List<ChunkPlan>();
                long size = 0;
                foreach (var (offset, data) in cdc.Split(input))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sha.AppendData(data.Span);
                    chunks.Add(new ChunkPlan(keys.ChunkId(data.Span), offset, data.Length));
                    size += data.Length;
                }

                result.Add(new FilePlan(entry, size, new DateTimeOffset(modified, TimeSpan.Zero), Convert.ToHexStringLower(sha.GetHashAndReset()), chunks));
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the snapshots on a destination (cached locally by name: snapshots never change) and returns the chunks
    /// that can be reused with <paramref name="keys"/> and all packs still referenced.
    /// </summary>
    /// <param name="strict">
    /// True for garbage collection: a snapshot that cannot be read (e.g. made with an earlier password) fails the
    /// call, so its packs are never deleted. False for backups: such snapshots are only skipped for reuse.
    /// </param>
    public static async Task<RepositoryState> LoadAsync(
        IBackupDestination destination, IReadOnlyCollection<string> snapshotNames, string cacheFolder, DedupKeys keys, Func<byte[], DedupKeys?> keysForSalt, bool strict, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheFolder);
        var known = new Dictionary<string, DedupChunk>(StringComparer.Ordinal);
        var packs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in snapshotNames)
        {
            var local = Path.Combine(cacheFolder, name);
            if (!File.Exists(local))
            {
                await destination.DownloadAsync(name, local + ".tmp", null, cancellationToken);
                File.Move(local + ".tmp", local, overwrite: true);
            }

            var sameKeys = DedupSnapshot.ReadSalt(local).AsSpan().SequenceEqual(keys.Salt);
            DedupSnapshot snapshot;
            try
            {
                snapshot = await DedupSnapshot.ReadAsync(local, keysForSalt, cancellationToken);
            }
            catch (Exception ex) when (!strict && ex is CryptographicException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var chunk in snapshot.Chunks)
            {
                packs.Add(chunk.Pack);
                if (sameKeys)
                {
                    known.TryAdd(chunk.Id, chunk);
                }
            }
        }

        // Forget cached snapshots that were deleted from the destination.
        foreach (var stale in Directory.GetFiles(cacheFolder).Where(f => !snapshotNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)))
        {
            File.Delete(stale);
        }

        return new RepositoryState(known, packs, [.. snapshotNames]);
    }

    /// <summary>Builds the snapshot for one destination, writing the chunks it does not have into new pack files.</summary>
    public static async Task<BuiltSnapshot> BuildAsync(
        IReadOnlyList<FilePlan> files, IReadOnlyDictionary<string, DedupChunk> known, DedupKeys keys, string prefix, string folder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var snapshot = new DedupSnapshot();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var packs = new List<(string Path, string Name, long Size)>();
        FileStream? pack = null;
        string? packName = null;
        long newBytes = 0;
        int created = 0, reused = 0;

        async Task ClosePackAsync()
        {
            if (pack is not null)
            {
                await pack.FlushAsync(cancellationToken);
                packs.Add((pack.Name, packName!, pack.Length));
                await pack.DisposeAsync();
                pack = null;
            }
        }

        try
        {
            foreach (var file in files)
            {
                var entry = new DedupFile { Path = file.Entry.EntryName, Size = file.Size, Modified = file.Modified, Sha256 = file.Sha256 };
                FileStream? source = null;
                try
                {
                    foreach (var chunk in file.Chunks)
                    {
                        if (!positions.TryGetValue(chunk.Id, out var position))
                        {
                            if (known.TryGetValue(chunk.Id, out var existing))
                            {
                                reused++;
                                snapshot.Chunks.Add(existing);
                            }
                            else
                            {
                                source ??= new FileStream(file.Entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                var data = new byte[chunk.Size];
                                source.Position = chunk.Offset;
                                await source.ReadExactlyAsync(data, cancellationToken);
                                if (keys.ChunkId(data) != chunk.Id)
                                {
                                    throw new IOException($"'{file.Entry.SourcePath}' changed during the backup. Use Volume Shadow Copy or stop the application first.");
                                }

                                var blob = keys.Seal(data);
                                if (pack is null || pack.Length + blob.Length > PackTargetSize)
                                {
                                    await ClosePackAsync();
                                    packName = NewPackName(prefix);
                                    pack = new FileStream(Path.Combine(folder, packName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                                }

                                snapshot.Chunks.Add(new DedupChunk { Id = chunk.Id, Pack = packName!, Offset = pack.Length, Length = blob.Length, Size = chunk.Size });
                                await pack.WriteAsync(blob, cancellationToken);
                                newBytes += blob.Length;
                                created++;
                            }

                            position = snapshot.Chunks.Count - 1;
                            positions[chunk.Id] = position;
                        }
                        else
                        {
                            reused++;
                        }

                        entry.Chunks.Add(position);
                    }
                }
                finally
                {
                    if (source is not null)
                    {
                        await source.DisposeAsync();
                    }
                }

                snapshot.Files.Add(entry);
            }

            await ClosePackAsync();
        }
        finally
        {
            if (pack is not null)
            {
                await pack.DisposeAsync();
            }
        }

        return new BuiltSnapshot(snapshot, packs, newBytes, created, reused);
    }

    /// <summary>Rebuilds the files of a snapshot and checks each one against its SHA-256.</summary>
    /// <param name="fetchPack">Returns a local copy of a pack (downloads it when needed).</param>
    public static async Task<(IReadOnlyList<string> Files, long Bytes)> RestoreAsync(
        DedupSnapshot snapshot,
        DedupKeys keys,
        Func<string, CancellationToken, Task<string>> fetchPack,
        string targetDirectory,
        bool overwrite,
        IReadOnlyCollection<string>? include,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(targetDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        var plan = new List<(DedupFile File, string Path)>();
        foreach (var file in snapshot.Files)
        {
            if (include is { Count: > 0 } && !include.Any(i => i.EndsWith('/') ? file.Path.StartsWith(i, StringComparison.OrdinalIgnoreCase) : string.Equals(file.Path, i, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(root, file.Path.Replace('\\', '/')));
            if (!destination.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The snapshot contains an unsafe path: '{file.Path}'.");
            }

            if (!overwrite && File.Exists(destination))
            {
                throw new IOException($"'{destination}' already exists. Choose an empty folder or enable overwrite.");
            }

            plan.Add((file, destination));
        }

        var restored = new List<string>();
        long bytes = 0;
        var buffers = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (file, destination) in plan)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    foreach (var index in file.Chunks)
                    {
                        var chunk = snapshot.Chunks[index];
                        if (!buffers.TryGetValue(chunk.Pack, out var pack))
                        {
                            buffers[chunk.Pack] = pack = File.OpenRead(await fetchPack(chunk.Pack, cancellationToken));
                        }

                        var blob = new byte[chunk.Length];
                        pack.Position = chunk.Offset;
                        await pack.ReadExactlyAsync(blob, cancellationToken);
                        var data = keys.Open(blob, chunk.Size);
                        sha.AppendData(data);
                        await output.WriteAsync(data, cancellationToken);
                    }
                }

                if (Convert.ToHexStringLower(sha.GetHashAndReset()) != file.Sha256)
                {
                    throw new InvalidDataException($"'{file.Path}' does not match its checksum after restore: the backup is corrupted.");
                }

                File.SetLastWriteTimeUtc(destination, file.Modified.UtcDateTime);
                restored.Add(file.Path);
                bytes += file.Size;
            }
        }
        finally
        {
            foreach (var pack in buffers.Values)
            {
                await pack.DisposeAsync();
            }
        }

        return (restored, bytes);
    }
}
