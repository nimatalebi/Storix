using System.IO.Compression;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Engine;

/// <param name="TargetDirectory">Folder the archive content is extracted to.</param>
/// <param name="Password">Encryption password (required for <c>.zip.aes</c> backups).</param>
/// <param name="Overwrite">Replace files that already exist in the target folder.</param>
/// <param name="VerifyChecksum">Check the SHA-256 sidecar before restoring (when present).</param>
public sealed record RestoreRequest(string TargetDirectory, string? Password = null, bool Overwrite = false, bool VerifyChecksum = true)
{
    /// <summary>
    /// Restore only these archive paths (files, or folders ending with '/'). Null or empty restores everything.
    /// </summary>
    public IReadOnlyCollection<string>? Include { get; init; }
}

public sealed record RestoreResult(IReadOnlyList<string> Files, long TotalBytes, bool ChecksumVerified);

/// <summary>Downloads, verifies, decrypts and extracts backups.</summary>
public sealed class RestoreService(IDestinationFactory destinationFactory)
{
    /// <summary>Lists the backups of a job stored on a destination, newest first.</summary>
    public async Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync(BackupJob job, DestinationDefinition destination, CancellationToken cancellationToken)
    {
        await using var target = destinationFactory.Create(destination);
        var files = await target.ListAsync(cancellationToken);
        return BackupNaming.ParseBackups(job.FilePrefix, files.Select(f => f.Name)).OrderByDescending(b => b.CreatedAt).ToList();
    }

    /// <summary>Downloads a backup (and its checksum) from a destination and restores it.</summary>
    public async Task<RestoreResult> RestoreFromDestinationAsync(
        DestinationDefinition destination,
        string backupName,
        RestoreRequest request,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        var work = CreateWorkDirectory();
        try
        {
            await using var target = destinationFactory.Create(destination);
            var names = (await target.ListAsync(cancellationToken)).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Deduplicated backup: the snapshot plus the packs holding its chunks.
            if (Dedup.DedupEngine.IsSnapshot(backupName))
            {
                var snapshotPath = Path.Combine(work, backupName);
                status?.Report($"Downloading {backupName} from {destination.Name}...");
                await target.DownloadAsync(backupName, snapshotPath, null, cancellationToken);
                return await RestoreSnapshotAsync(snapshotPath, request, async (pack, ct) =>
                {
                    var local = Path.Combine(work, pack);
                    if (!File.Exists(local))
                    {
                        status?.Report($"Downloading {pack}...");
                        await target.DownloadAsync(pack, local, null, ct);
                    }

                    return local;
                }, status, cancellationToken);
            }

            // An incremental backup needs every backup back to its full one.
            var members = new[] { backupName }.AsEnumerable();
            if (BackupNaming.IsIncremental(backupName) && BackupNaming.PrefixOf(backupName) is { } prefix)
            {
                members = BackupNaming.ChainOf(BackupNaming.ParseBackups(prefix, names), backupName).Select(b => b.Name);
            }

            string? local = null;
            foreach (var member in members)
            {
                local = await DownloadBackupAsync(target, names, member, destination.Name, work, status, cancellationToken);
            }

            return await RestoreFromFileAsync(local!, request, status, cancellationToken);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>Downloads a backup (joining volumes) and its checksum into <paramref name="work"/>.</summary>
    private static async Task<string> DownloadBackupAsync(
        IBackupDestination target, IReadOnlySet<string> names, string backupName, string destinationName, string work, IProgress<string>? status, CancellationToken cancellationToken)
    {
        var local = Path.Combine(work, backupName);
        if (names.Contains(backupName))
        {
            status?.Report($"Downloading {backupName} from {destinationName}...");
            await target.DownloadAsync(backupName, local, null, cancellationToken);
        }
        else if (names.Contains(backupName + ChunkManifest.Extension))
        {
            var manifestPath = local + ChunkManifest.Extension;
            await target.DownloadAsync(backupName + ChunkManifest.Extension, manifestPath, null, cancellationToken);
            var manifest = ChunkManifest.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
            await ChunkedArchive.JoinAsync(manifest, async (chunk, ct) =>
            {
                status?.Report($"Downloading volume {chunk.Name}...");
                var path = Path.Combine(work, chunk.Name);
                await target.DownloadAsync(chunk.Name, path, null, ct);
                return path;
            }, local, deleteChunks: true, cancellationToken);
            File.Delete(manifestPath);
        }
        else
        {
            throw new FileNotFoundException($"Backup '{backupName}' was not found on '{destinationName}'.");
        }

        var sidecar = backupName + Checksum.SidecarExtension;
        if (names.Contains(sidecar))
        {
            await target.DownloadAsync(sidecar, local + Checksum.SidecarExtension, null, cancellationToken);
        }

        return local;
    }

    /// <summary>
    /// Restores a backup file that is already on disk (e.g. copied from a local or UNC folder). For an incremental
    /// backup, the backups it depends on must be in the same folder; they are restored first, oldest first.
    /// </summary>
    public static async Task<RestoreResult> RestoreFromFileAsync(string archivePath, RestoreRequest request, IProgress<string>? status, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;

        // Parts downloaded from an archive-only destination (Telegram): name.001, name.002... are joined first.
        if (TelegramParts(archivePath) is { } parts)
        {
            var joinFolder = CreateWorkDirectory();
            try
            {
                var joined = Path.Combine(joinFolder, parts.Name);
                status?.Report($"Joining {parts.Files.Count} part(s)...");
                await using (var output = new FileStream(joined, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamCopy.BufferSize, useAsync: true))
                {
                    foreach (var part in parts.Files)
                    {
                        await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, StreamCopy.BufferSize, useAsync: true);
                        await input.CopyToAsync(output, cancellationToken);
                    }
                }

                var partSidecar = Path.Combine(folder, parts.Name + Checksum.SidecarExtension);
                if (File.Exists(partSidecar))
                {
                    File.Copy(partSidecar, joined + Checksum.SidecarExtension);
                }

                return await RestoreFromFileAsync(joined, request, status, cancellationToken);
            }
            finally
            {
                TryDelete(joinFolder);
            }
        }

        if (Dedup.DedupEngine.IsSnapshot(archivePath))
        {
            // The packs must be in the same folder as the snapshot (e.g. a local or network destination).
            return await RestoreSnapshotAsync(archivePath, request, (pack, _) =>
            {
                var path = Path.Combine(folder, pack);
                return File.Exists(path) ? Task.FromResult(path) : throw new FileNotFoundException($"Pack '{pack}' is missing next to the snapshot.", path);
            }, status, cancellationToken);
        }

        var logical = LogicalName(Path.GetFileName(archivePath));
        if (!BackupNaming.IsIncremental(logical) || BackupNaming.PrefixOf(logical) is not { } prefix)
        {
            return await RestoreArchiveAsync(archivePath, request, status, cancellationToken);
        }

        var chain = BackupNaming.ChainOf(BackupNaming.ParseBackups(prefix, Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f))), logical);
        var files = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        var verified = true;
        for (var i = 0; i < chain.Count; i++)
        {
            status?.Report($"Restoring {i + 1} of {chain.Count}: {chain[i].Name}...");
            var path = Path.Combine(folder, chain[i].Name);
            if (!File.Exists(path))
            {
                path += ChunkManifest.Extension;
            }

            // Later backups in the chain hold newer versions of the same files.
            var result = await RestoreArchiveAsync(path, i == 0 ? request : request with { Overwrite = true }, status, cancellationToken);
            files.UnionWith(result.Files);
            bytes += result.TotalBytes;
            verified &= result.ChecksumVerified;
        }

        return new RestoreResult([.. files.Order(StringComparer.Ordinal)], bytes, verified);
    }

    private static async Task<RestoreResult> RestoreSnapshotAsync(
        string snapshotPath, RestoreRequest request, Func<string, CancellationToken, Task<string>> fetchPack, IProgress<string>? status, CancellationToken cancellationToken)
    {
        var keysForSalt = Dedup.DedupEngine.KeyCache(string.IsNullOrEmpty(request.Password) ? null : request.Password);
        status?.Report("Reading the snapshot...");
        var snapshot = await Dedup.DedupSnapshot.ReadAsync(snapshotPath, keysForSalt, cancellationToken);
        var salt = Dedup.DedupSnapshot.ReadSalt(snapshotPath);
        var keys = salt.Length == 0 ? Dedup.DedupKeys.Plain : keysForSalt(salt)!;

        status?.Report($"Restoring to {request.TargetDirectory}...");
        var (files, bytes) = await Dedup.DedupEngine.RestoreAsync(snapshot, keys, fetchPack, request.TargetDirectory, request.Overwrite, request.Include, cancellationToken);
        status?.Report($"Restored {files.Count} file(s).");

        // Every file was checked against its SHA-256 while restoring.
        return new RestoreResult(files, bytes, ChecksumVerified: true);
    }

    /// <summary>
    /// For a file named <c>backup.zip.aes.002</c> (or .001...), the backup name and all its parts in order.
    /// Null when the file is not a part or when a part is missing.
    /// </summary>
    internal static (string Name, IReadOnlyList<string> Files)? TelegramParts(string path)
    {
        var fileName = Path.GetFileName(path);
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || fileName.Length - dot != 4 || !fileName[(dot + 1)..].All(char.IsAsciiDigit))
        {
            return null;
        }

        var name = fileName[..dot];
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var files = new List<string>();
        for (var index = 1; index <= 999 && File.Exists(Path.Combine(folder, $"{name}.{index:D3}")); index++)
        {
            files.Add(Path.Combine(folder, $"{name}.{index:D3}"));
        }

        if (files.Count == 0)
        {
            return null;
        }

        // A gap (e.g. .003 without .002) means a part was not downloaded.
        if (Directory.EnumerateFiles(folder, name + ".*").Count(f => TelegramPartIndex(f, name) > files.Count) > 0)
        {
            throw new FileNotFoundException($"A part of '{name}' is missing: download every part (.001, .002, ...) into the same folder.");
        }

        return (name, files);
    }

    private static int TelegramPartIndex(string path, string name)
    {
        var suffix = Path.GetFileName(path)[name.Length..];
        return suffix.Length == 4 && suffix[0] == '.' && int.TryParse(suffix[1..], out var index) ? index : 0;
    }

    /// <summary>The backup name of an archive, manifest or volume file name.</summary>
    private static string LogicalName(string fileName)
    {
        if (fileName.EndsWith(ChunkManifest.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^ChunkManifest.Extension.Length];
        }

        var index = fileName.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        return index > 0 && fileName.Length > index + 5 && fileName[(index + 5)..].All(char.IsAsciiDigit) ? fileName[..index] : fileName;
    }

    private static async Task<RestoreResult> RestoreArchiveAsync(string archivePath, RestoreRequest request, IProgress<string>? status, CancellationToken cancellationToken)
    {
        // Split backups: accept the manifest or any volume and join the set first.
        if (TryResolveVolumeSet(archivePath, out var manifestPath))
        {
            var joinFolder = CreateWorkDirectory();
            try
            {
                var manifest = ChunkManifest.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
                var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
                var joined = Path.Combine(joinFolder, manifest.File);
                status?.Report($"Joining {manifest.Chunks.Count} volume(s)...");
                await ChunkedArchive.JoinAsync(manifest, (chunk, _) => Task.FromResult(Path.Combine(folder, chunk.Name)), joined, deleteChunks: false, cancellationToken);

                var volumeSidecar = Path.Combine(folder, manifest.File + Checksum.SidecarExtension);
                if (File.Exists(volumeSidecar))
                {
                    File.Copy(volumeSidecar, joined + Checksum.SidecarExtension);
                }

                return await RestoreArchiveAsync(joined, request, status, cancellationToken);
            }
            finally
            {
                TryDelete(joinFolder);
            }
        }

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Backup file not found.", archivePath);
        }

        var checksumVerified = false;
        var sidecar = archivePath + Checksum.SidecarExtension;
        if (request.VerifyChecksum && File.Exists(sidecar))
        {
            status?.Report("Verifying SHA-256 checksum...");
            var expected = (await File.ReadAllTextAsync(sidecar, cancellationToken)).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var actual = await Checksum.Sha256Async(archivePath, cancellationToken);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Checksum mismatch: the backup file is corrupted (expected {expected}, got {actual}).");
            }

            checksumVerified = true;
        }

        var work = CreateWorkDirectory();
        try
        {
            var zipPath = archivePath;
            if (AesFileEncryptor.IsEncryptedFile(archivePath))
            {
                if (string.IsNullOrEmpty(request.Password))
                {
                    throw new UnauthorizedAccessException("This backup is encrypted. Enter the encryption password.");
                }

                status?.Report("Decrypting...");
                zipPath = Path.Combine(work, "backup.zip");
                await AesFileEncryptor.DecryptAsync(archivePath, zipPath, request.Password, cancellationToken);
            }

            status?.Report($"Extracting to {request.TargetDirectory}...");
            var (files, bytes) = await ExtractAsync(zipPath, request.TargetDirectory, request.Overwrite, cancellationToken, request.Include);
            status?.Report($"Restored {files.Count} file(s).");
            return new RestoreResult(files, bytes, checksumVerified);
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static bool IsIncluded(string entry, IReadOnlyCollection<string> include) =>
        include.Any(i => i.EndsWith('/')
            ? entry.StartsWith(i, StringComparison.OrdinalIgnoreCase)
            : string.Equals(entry, i, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the file list of a backup. Uses the small index file when present; older backups are downloaded
    /// and their ZIP directory is read instead.
    /// </summary>
    public async Task<BackupIndex> GetIndexAsync(DestinationDefinition destination, string backupName, string? secret, CancellationToken cancellationToken)
    {
        var work = CreateWorkDirectory();
        try
        {
            await using var target = destinationFactory.Create(destination);
            var names = (await target.ListAsync(cancellationToken)).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Dedup.DedupEngine.IsSnapshot(backupName))
            {
                var snapshotPath = Path.Combine(work, backupName);
                await target.DownloadAsync(backupName, snapshotPath, null, cancellationToken);
                var snapshot = await Dedup.DedupSnapshot.ReadAsync(snapshotPath, Dedup.DedupEngine.KeyCache(secret), cancellationToken);
                return snapshot.ToIndex(backupName);
            }

            if (names.Contains(backupName + BackupIndex.Extension))
            {
                var path = Path.Combine(work, backupName + BackupIndex.Extension);
                await target.DownloadAsync(backupName + BackupIndex.Extension, path, null, cancellationToken);
                return await BackupIndex.ReadAsync(path, secret, cancellationToken);
            }
        }
        finally
        {
            TryDelete(work);
        }

        // No index (older backups): download the backup and read the ZIP directory.
        var temp = CreateWorkDirectory();
        try
        {
            var zip = await DownloadAsZipAsync(destination, backupName, secret, temp, cancellationToken);
            return await BackupIndex.FromZipAsync(zip, backupName, cancellationToken);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task<string> DownloadAsZipAsync(DestinationDefinition destination, string backupName, string? secret, string folder, CancellationToken cancellationToken)
    {
        var request = new RestoreRequest(Path.Combine(folder, "unused"), secret) { Include = ["\0"] };
        await using var target = destinationFactory.Create(destination);
        var local = Path.Combine(folder, backupName);
        var names = (await target.ListAsync(cancellationToken)).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Contains(backupName))
        {
            await target.DownloadAsync(backupName, local, null, cancellationToken);
        }
        else
        {
            var manifestPath = local + ChunkManifest.Extension;
            await target.DownloadAsync(backupName + ChunkManifest.Extension, manifestPath, null, cancellationToken);
            var manifest = ChunkManifest.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
            await ChunkedArchive.JoinAsync(manifest, async (chunk, ct) =>
            {
                var path = Path.Combine(folder, chunk.Name);
                await target.DownloadAsync(chunk.Name, path, null, ct);
                return path;
            }, local, deleteChunks: true, cancellationToken);
        }

        if (!AesFileEncryptor.IsEncryptedFile(local))
        {
            return local;
        }

        if (string.IsNullOrEmpty(secret))
        {
            throw new UnauthorizedAccessException("This backup is encrypted. Enter the encryption password.");
        }

        var zip = Path.Combine(folder, "backup.zip");
        await AesFileEncryptor.DecryptAsync(local, zip, secret, cancellationToken);
        return zip;
    }

    private static bool TryResolveVolumeSet(string path, out string manifestPath)
    {
        manifestPath = path;
        if (path.EndsWith(ChunkManifest.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(path);
        }

        var name = Path.GetFileName(path);
        var index = name.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        if (index > 0 && name[(index + 5)..].All(char.IsAsciiDigit) && name.Length > index + 5)
        {
            manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name[..index] + ChunkManifest.Extension);
            return File.Exists(manifestPath);
        }

        return false;
    }

    internal static async Task<(IReadOnlyList<string> Files, long Bytes)> ExtractAsync(
        string zipPath, string targetDirectory, bool overwrite, CancellationToken cancellationToken, IReadOnlyCollection<string>? include = null)
    {
        var root = Path.GetFullPath(targetDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        using var zip = await ArchiveBuilder.OpenReadAsync(zipPath, cancellationToken);

        // Validate every path first so a malicious archive cannot write anything outside the target.
        var plan = new List<(ZipArchiveEntry Entry, string Path)>();
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.StartsWith(ChainInfo.MetadataFolder, StringComparison.Ordinal))
            {
                continue;
            }

            if (include is { Count: > 0 } && !IsIncluded(entry.FullName, include))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('\\', '/')));
            if (!destination.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The archive contains an unsafe path: '{entry.FullName}'.");
            }

            plan.Add((entry, destination));
        }

        if (!overwrite && plan.FirstOrDefault(p => !p.Entry.FullName.EndsWith('/') && File.Exists(p.Path)) is { Path: not null } existing)
        {
            throw new IOException($"'{existing.Path}' already exists. Choose an empty folder or enable overwrite.");
        }

        var files = new List<string>();
        long bytes = 0;
        foreach (var (entry, destination) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = entry.Open())
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, StreamCopy.BufferSize, useAsync: true))
            {
                await StreamCopy.CopyAsync(input, output, null, cancellationToken);
            }

            File.SetLastWriteTime(destination, entry.LastWriteTime.DateTime);
            files.Add(entry.FullName);
            bytes += entry.Length;
        }

        // Incremental backups list the files deleted since the backup before them.
        if (zip.GetEntry(ChainInfo.DeletedEntryName) is { } deletedEntry)
        {
            using var reader = new StreamReader(deletedEntry.Open());
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0 || (include is { Count: > 0 } && !IsIncluded(line, include)))
                {
                    continue;
                }

                var path = Path.GetFullPath(Path.Combine(root, line.Replace('\\', '/')));
                if (path.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        return (files, bytes);
    }

    private static string CreateWorkDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "storix-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
