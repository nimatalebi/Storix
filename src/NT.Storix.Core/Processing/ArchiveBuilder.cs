using System.IO.Compression;
using NT.Storix.Core.Models;
using ZstdSharp;

namespace NT.Storix.Core.Processing;

public sealed record ArchiveResult(string Path, int EntryCount, int SkippedCount, long SizeBytes)
{
    /// <summary>Files written into the archive (for the backup index).</summary>
    public IReadOnlyList<IndexEntry> Entries { get; init; } = [];
}

/// <summary>
/// Builds ZIP (Zip64 capable) archives by streaming files from disk. With zstd the entries are stored
/// uncompressed and the whole ZIP is wrapped in one Zstandard frame (<c>.zip.zst</c>, readable with
/// <c>zstd -d</c> and any ZIP tool).
/// </summary>
public static class ArchiveBuilder
{
    private const int BufferSize = 1024 * 1024;

    public const string ZstdExtension = ".zst";

    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    public static bool UsesZstd(ArchiveCompression compression) => compression is ArchiveCompression.Zstd or ArchiveCompression.ZstdSmallest;

    /// <summary>True when the file starts with a Zstandard frame.</summary>
    public static bool IsZstdFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[4];
        return stream.ReadAtLeast(header, 4, throwOnEndOfStream: false) == 4 && header.SequenceEqual(ZstdMagic);
    }

    /// <summary>
    /// Opens a backup archive for reading, plain or zstd-wrapped. A zstd archive is decompressed to a temporary
    /// file that is deleted when the returned archive is disposed.
    /// </summary>
    public static async Task<ZipArchive> OpenReadAsync(string archivePath, CancellationToken cancellationToken)
    {
        if (!IsZstdFile(archivePath))
        {
            var plain = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            return new ZipArchive(plain, ZipArchiveMode.Read, leaveOpen: false);
        }

        // Next to the archive (usually the staging or work folder, which has room); the temp folder when read-only.
        FileStream output;
        try
        {
            output = CreateTempFile(Path.GetDirectoryName(Path.GetFullPath(archivePath))!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output = CreateTempFile(Path.GetTempPath());
        }

        try
        {
            await using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            await using (var zstd = new DecompressionStream(input))
            {
                await zstd.CopyToAsync(output, BufferSize, cancellationToken);
            }

            output.Position = 0;
            return new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    private static FileStream CreateTempFile(string folder) =>
        new(Path.Combine(folder, $".{Guid.NewGuid():N}.zip.tmp"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    public static async Task<ArchiveResult> CreateAsync(
        IReadOnlyCollection<ArchiveEntry> entries,
        string archivePath,
        ArchiveCompression compression,
        Action<string>? warn,
        CancellationToken cancellationToken)
    {
        var level = ToLevel(compression);
        var written = 0;
        var skipped = 0;
        var index = new List<IndexEntry>(entries.Count);

        await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize, useAsync: true))
        await using (var body = UsesZstd(compression) ? new CompressionStream(output, compression == ArchiveCompression.ZstdSmallest ? 19 : 3, BufferSize, leaveOpen: true) : null)
        await using (var zip = new ZipArchive((Stream?)body ?? output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileStream input;
                try
                {
                    input = new FileStream(entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
                }
                catch (Exception ex) when (entry.Optional && ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    warn?.Invoke($"Skipped '{entry.SourcePath}': {ex.Message}");
                    continue;
                }

                await using (input)
                {
                    var zipEntry = zip.CreateEntry(entry.EntryName, level);
                    zipEntry.LastWriteTime = File.GetLastWriteTime(entry.SourcePath);
                    long size;
                    await using (var target = zipEntry.Open())
                    {
                        await input.CopyToAsync(target, BufferSize, cancellationToken);
                        size = input.Position;
                    }

                    index.Add(new IndexEntry { Path = entry.EntryName, Size = size, Modified = zipEntry.LastWriteTime });
                }

                written++;
            }
        }

        return new ArchiveResult(archivePath, written, skipped, new FileInfo(archivePath).Length) { Entries = index };
    }

    /// <summary>Reads every entry of the archive to make sure it is complete and not corrupted.</summary>
    public static async Task VerifyAsync(string archivePath, int expectedEntries, CancellationToken cancellationToken)
    {
        using var zip = await OpenReadAsync(archivePath, cancellationToken);

        if (zip.Entries.Count != expectedEntries)
        {
            throw new InvalidDataException($"Archive contains {zip.Entries.Count} entries, expected {expectedEntries}.");
        }

        var buffer = new byte[BufferSize];
        foreach (var entry in zip.Entries)
        {
            await using var entryStream = entry.Open();
            long total = 0;
            int read;
            while ((read = await entryStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
            }

            if (total != entry.Length)
            {
                throw new InvalidDataException($"Entry '{entry.FullName}' is truncated ({total} of {entry.Length} bytes).");
            }
        }
    }

    private static CompressionLevel ToLevel(ArchiveCompression compression) => compression switch
    {
        ArchiveCompression.None or ArchiveCompression.Zstd or ArchiveCompression.ZstdSmallest => CompressionLevel.NoCompression,
        ArchiveCompression.Fastest => CompressionLevel.Fastest,
        ArchiveCompression.Smallest => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };
}
