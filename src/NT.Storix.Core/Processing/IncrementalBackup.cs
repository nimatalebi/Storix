using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NT.Storix.Core.Models;
using NT.Storix.Core.Security;

namespace NT.Storix.Core.Processing;

public sealed class FileStateEntry
{
    public long Size { get; set; }

    /// <summary>Last write time, UTC ticks.</summary>
    public long Modified { get; set; }
}

/// <summary>Files of the last backup of an incremental job, kept in the local database.</summary>
public sealed class IncrementalState
{
    public int Version { get; set; } = 1;

    /// <summary>The backup this state describes; the next incremental backup is based on it.</summary>
    public string BaseArchive { get; set; } = string.Empty;

    public DateTimeOffset LastFullAt { get; set; }

    /// <summary>Encryption and format settings of the chain; a change starts a new full backup.</summary>
    public string Settings { get; set; } = string.Empty;

    public Dictionary<string, FileStateEntry> Files { get; set; } = new(StringComparer.Ordinal);

    public byte[] ToBytes()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(gzip, this, StorixJson.Options);
        }

        return buffer.ToArray();
    }

    public static IncrementalState? FromBytes(byte[]? data)
    {
        if (data is null)
        {
            return null;
        }

        try
        {
            using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            var state = JsonSerializer.Deserialize<IncrementalState>(gzip, StorixJson.Options);
            if (state is not null)
            {
                state.Files = new Dictionary<string, FileStateEntry>(state.Files, StringComparer.Ordinal);
            }

            return state;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return null; // Unreadable state: the next backup is simply a full one.
        }
    }
}

/// <summary>Metadata stored inside every incremental archive as <c>.storix/chain.json</c>.</summary>
public sealed class ChainInfo
{
    public const string EntryName = ".storix/chain.json";
    public const string DeletedEntryName = ".storix/deleted.txt";

    /// <summary>Archive entries under this folder are Storix metadata, never restored as files.</summary>
    public const string MetadataFolder = ".storix/";

    public string Kind { get; set; } = "incremental";

    public string BasedOn { get; set; } = string.Empty;

    public int DeletedCount { get; set; }
}

public sealed record IncrementalPlan(bool Full, string? Reason, IReadOnlyList<ArchiveEntry> Entries, IReadOnlyList<string> Deleted, IncrementalState State);

/// <summary>
/// Decides between a full and an incremental backup and, for an incremental one, which files changed (size or
/// modification time) and which were deleted since the previous backup.
/// </summary>
public static class IncrementalPlanner
{
    public static IncrementalPlan Plan(
        IReadOnlyCollection<ArchiveEntry> entries,
        IncrementalState? previous,
        string? lastSuccessfulArchive,
        string settings,
        int fullEveryDays,
        DateTimeOffset now,
        Func<ArchiveEntry, FileStateEntry?> stat)
    {
        var files = new Dictionary<string, FileStateEntry>(StringComparer.Ordinal);
        var current = new List<(ArchiveEntry Entry, FileStateEntry? State)>(entries.Count);
        foreach (var entry in entries)
        {
            var state = stat(entry);
            current.Add((entry, state));
            if (state is not null)
            {
                files[entry.EntryName] = state;
            }
        }

        var reason = previous is null ? "no previous file list"
            : !string.Equals(previous.BaseArchive, lastSuccessfulArchive, StringComparison.OrdinalIgnoreCase) ? "the previous backup did not complete on every destination"
            : previous.Settings != settings ? "encryption or format settings changed"
            : fullEveryDays > 0 && now - previous.LastFullAt >= TimeSpan.FromDays(fullEveryDays) ? $"last full backup is {fullEveryDays}+ day(s) old"
            : null;

        if (reason is not null)
        {
            return new IncrementalPlan(true, reason, entries.ToList(), [], new IncrementalState { LastFullAt = now, Settings = settings, Files = files });
        }

        var changed = current
            .Where(c => c.State is null
                        || !previous!.Files.TryGetValue(c.Entry.EntryName, out var old)
                        || old.Size != c.State.Size
                        || old.Modified != c.State.Modified)
            .Select(c => c.Entry)
            .ToList();
        var names = entries.Select(e => e.EntryName).ToHashSet(StringComparer.Ordinal);
        var deleted = previous!.Files.Keys.Where(k => !names.Contains(k)).Order(StringComparer.Ordinal).ToList();
        return new IncrementalPlan(false, null, changed, deleted, new IncrementalState { LastFullAt = previous.LastFullAt, Settings = settings, Files = files });
    }

    /// <summary>
    /// Identifies the encryption secret and archive format without storing the secret: a PBKDF2 hash salted
    /// with the job id, as strong as the key derivation of the archives themselves.
    /// </summary>
    public static string SettingsFingerprint(Guid jobId, ProcessingOptions processing)
    {
        var secret = processing.Encrypt ? EncryptionSecret.Resolve(processing) ?? string.Empty : string.Empty;
        var text = $"{processing.Encrypt}|{processing.EncryptionMode}|{ArchiveBuilder.UsesZstd(processing.Compression)}|{secret}";
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(text), jobId.ToByteArray(), 600_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToHexString(hash);
    }

    public static FileStateEntry? Stat(ArchiveEntry entry)
    {
        try
        {
            var info = new FileInfo(entry.SourcePath);
            return info.Exists ? new FileStateEntry { Size = info.Length, Modified = info.LastWriteTimeUtc.Ticks } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
