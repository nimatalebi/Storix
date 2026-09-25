using System.IO.Compression;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Engine;

/// <param name="TargetDirectory">Folder the archive content is extracted to.</param>
/// <param name="Password">Encryption password (required for <c>.zip.aes</c> backups).</param>
/// <param name="Overwrite">Replace files that already exist in the target folder.</param>
/// <param name="VerifyChecksum">Check the SHA-256 sidecar before restoring (when present).</param>
public sealed record RestoreRequest(string TargetDirectory, string? Password = null, bool Overwrite = false, bool VerifyChecksum = true);

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
            if (!names.Contains(backupName))
            {
                throw new FileNotFoundException($"Backup '{backupName}' was not found on '{destination.Name}'.");
            }

            var local = Path.Combine(work, backupName);
            status?.Report($"Downloading {backupName} from {destination.Name}...");
            await target.DownloadAsync(backupName, local, null, cancellationToken);

            var sidecar = backupName + Checksum.SidecarExtension;
            if (names.Contains(sidecar))
            {
                await target.DownloadAsync(sidecar, local + Checksum.SidecarExtension, null, cancellationToken);
            }

            return await RestoreFromFileAsync(local, request, status, cancellationToken);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>Restores a backup file that is already on disk (e.g. copied from a local or UNC folder).</summary>
    public static async Task<RestoreResult> RestoreFromFileAsync(string archivePath, RestoreRequest request, IProgress<string>? status, CancellationToken cancellationToken)
    {
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
            var (files, bytes) = await ExtractAsync(zipPath, request.TargetDirectory, request.Overwrite, cancellationToken);
            status?.Report($"Restored {files.Count} file(s).");
            return new RestoreResult(files, bytes, checksumVerified);
        }
        finally
        {
            TryDelete(work);
        }
    }

    internal static async Task<(IReadOnlyList<string> Files, long Bytes)> ExtractAsync(string zipPath, string targetDirectory, bool overwrite, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(targetDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        await using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamCopy.BufferSize, useAsync: true);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        // Validate every path first so a malicious archive cannot write anything outside the target.
        var plan = new List<(ZipArchiveEntry Entry, string Path)>();
        foreach (var entry in zip.Entries)
        {
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

        return (files, bytes);
    }

    private static string CreateWorkDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "storix-restore", Guid.NewGuid().ToString("N"));
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
