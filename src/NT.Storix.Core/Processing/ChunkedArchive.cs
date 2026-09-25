using System.Security.Cryptography;
using System.Text.Json;

namespace NT.Storix.Core.Processing;

public sealed class ChunkInfo
{
    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// Describes a backup stored as volumes (<c>name.part0001</c>, <c>name.part0002</c>, ...). The manifest is
/// uploaded last, so its presence marks a complete backup.
/// </summary>
public sealed class ChunkManifest
{
    public const string FormatName = "storix-chunks";
    public const int CurrentVersion = 1;
    public const string Extension = ".manifest.json";

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Name of the logical (joined) backup file.</summary>
    public string File { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public long ChunkSize { get; set; }

    public List<ChunkInfo> Chunks { get; set; } = [];

    public static string PartName(string file, int index) => $"{file}.part{index:0000}";

    public string ToJson() => JsonSerializer.Serialize(this, StorixJson.Indented);

    public static ChunkManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<ChunkManifest>(json, StorixJson.Options);
        if (manifest is null || manifest.Format != FormatName || manifest.Chunks.Count == 0)
        {
            throw new InvalidDataException("Invalid Storix chunk manifest.");
        }

        if (manifest.Version > CurrentVersion)
        {
            throw new InvalidDataException($"The chunk manifest was written by a newer version of Storix (v{manifest.Version}).");
        }

        return manifest;
    }
}

/// <summary>Splits a backup into volumes and joins them back.</summary>
public static class ChunkedArchive
{
    /// <summary>Splits <paramref name="path"/> into chunks next to it and returns the manifest.</summary>
    public static async Task<ChunkManifest> SplitAsync(string path, long chunkSize, string sha256, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1024);
        var file = Path.GetFileName(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var manifest = new ChunkManifest { File = file, Size = new FileInfo(path).Length, Sha256 = sha256, ChunkSize = chunkSize };

        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamCopy.BufferSize, useAsync: true);
        var buffer = new byte[StreamCopy.BufferSize];
        var index = 0;
        while (input.Position < input.Length || index == 0)
        {
            index++;
            var name = ChunkManifest.PartName(file, index);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (var output = new FileStream(Path.Combine(directory, name), FileMode.Create, FileAccess.Write, FileShare.None, StreamCopy.BufferSize, useAsync: true))
            {
                while (written < chunkSize)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, chunkSize - written)), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                }
            }

            manifest.Chunks.Add(new ChunkInfo { Name = name, Size = written, Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()) });
            if (input.Length == 0)
            {
                break;
            }
        }

        return manifest;
    }

    /// <summary>
    /// Joins chunks into <paramref name="outputPath"/>, verifying every chunk and the whole file.
    /// </summary>
    /// <param name="locateChunk">Returns the local path of a chunk (it may download it first).</param>
    public static async Task JoinAsync(ChunkManifest manifest, Func<ChunkInfo, CancellationToken, Task<string>> locateChunk, string outputPath, bool deleteChunks, CancellationToken cancellationToken)
    {
        using var total = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamCopy.BufferSize, useAsync: true))
        {
            var buffer = new byte[StreamCopy.BufferSize];
            foreach (var chunk in manifest.Chunks)
            {
                var path = await locateChunk(chunk, cancellationToken);
                using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long size = 0;
                await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamCopy.BufferSize, useAsync: true))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        partHash.AppendData(buffer, 0, read);
                        total.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        size += read;
                    }
                }

                if (size != chunk.Size || !string.Equals(Convert.ToHexStringLower(partHash.GetHashAndReset()), chunk.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Chunk '{chunk.Name}' is corrupted or incomplete.");
                }

                if (deleteChunks)
                {
                    File.Delete(path);
                }
            }
        }

        if (!string.Equals(Convert.ToHexStringLower(total.GetHashAndReset()), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The joined backup does not match the checksum in the manifest.");
        }
    }
}
