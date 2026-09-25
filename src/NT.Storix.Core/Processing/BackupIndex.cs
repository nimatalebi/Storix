using System.IO.Compression;
using System.Text.Json;

namespace NT.Storix.Core.Processing;

public sealed class IndexEntry
{
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTimeOffset Modified { get; set; }
}

/// <summary>
/// List of the files inside a backup, stored next to it as <c>{archive}.index</c> (gzip JSON, encrypted like the
/// archive). Lets the manager browse, search and restore single files without downloading the whole backup.
/// </summary>
public sealed class BackupIndex
{
    public const string FormatName = "storix-index";
    public const int CurrentVersion = 1;
    public const string Extension = ".index";

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = CurrentVersion;

    public string Archive { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<IndexEntry> Entries { get; set; } = [];

    public long TotalSize => Entries.Sum(e => e.Size);

    /// <summary>Writes the index (gzip JSON) and encrypts it when <paramref name="secret"/> is set.</summary>
    public async Task WriteAsync(string path, string? secret, CancellationToken cancellationToken)
    {
        var plain = secret is null ? path : path + ".tmp";
        await using (var file = new FileStream(plain, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            await JsonSerializer.SerializeAsync(gzip, this, StorixJson.Options, cancellationToken);
        }

        if (secret is not null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            await AesFileEncryptor.EncryptAsync(plain, path, secret, cancellationToken);
            File.Delete(plain);
        }
    }

    /// <summary>Reads an index file (decrypting it when needed).</summary>
    public static async Task<BackupIndex> ReadAsync(string path, string? secret, CancellationToken cancellationToken)
    {
        var plain = path;
        string? temp = null;
        if (AesFileEncryptor.IsEncryptedFile(path))
        {
            if (string.IsNullOrEmpty(secret))
            {
                throw new UnauthorizedAccessException("The backup index is encrypted. Enter the encryption password.");
            }

            temp = path + ".plain";
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            await AesFileEncryptor.DecryptAsync(path, temp, secret, cancellationToken);
            plain = temp;
        }

        try
        {
            await using var file = File.OpenRead(plain);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var index = await JsonSerializer.DeserializeAsync<BackupIndex>(gzip, StorixJson.Options, cancellationToken);
            if (index is null || index.Format != FormatName)
            {
                throw new InvalidDataException("Invalid Storix backup index.");
            }

            return index;
        }
        finally
        {
            if (temp is not null)
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Builds an index from an existing (unencrypted) archive, for backups made before indexes existed.</summary>
    public static async Task<BackupIndex> FromZipAsync(string zipPath, string archiveName, CancellationToken cancellationToken)
    {
        using var zip = await ArchiveBuilder.OpenReadAsync(zipPath, cancellationToken);
        return new BackupIndex
        {
            Archive = archiveName,
            Entries = zip.Entries.Where(e => !e.FullName.EndsWith('/'))
                .Select(e => new IndexEntry { Path = e.FullName, Size = e.Length, Modified = e.LastWriteTime })
                .ToList(),
        };
    }

    /// <summary>Entries whose path contains <paramref name="text"/> (case-insensitive, wildcards * and ? allowed).</summary>
    public IEnumerable<IndexEntry> Search(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Entries;
        }

        var pattern = text.Trim();
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            return Entries.Where(e => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, e.Path, ignoreCase: true)
                                      || System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, System.IO.Path.GetFileName(e.Path), ignoreCase: true));
        }

        return Entries.Where(e => e.Path.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}
