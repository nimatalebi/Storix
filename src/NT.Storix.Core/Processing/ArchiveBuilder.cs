using System.IO.Compression;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Processing;

public sealed record ArchiveResult(string Path, int EntryCount, int SkippedCount, long SizeBytes)
{
    /// <summary>Files written into the archive (for the backup index).</summary>
    public IReadOnlyList<IndexEntry> Entries { get; init; } = [];
}

/// <summary>Builds ZIP (Zip64 capable) archives by streaming files from disk.</summary>
public static class ArchiveBuilder
{
    private const int BufferSize = 1024 * 1024;

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
        await using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
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
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

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
        ArchiveCompression.None => CompressionLevel.NoCompression,
        ArchiveCompression.Fastest => CompressionLevel.Fastest,
        ArchiveCompression.Smallest => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };
}
