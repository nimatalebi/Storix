using System.Globalization;
using System.Text.RegularExpressions;
using NT.Storix.Core.Destinations;

namespace NT.Storix.Core.Processing;

/// <summary>
/// Naming convention: <c>{prefix}_{yyyyMMdd_HHmmss}[.inc].zip[.zst][.aes]</c> (timestamp in UTC), plus <c>.sha256</c> sidecars.
/// <c>.inc</c> marks an incremental backup: it needs the backups before it, back to the previous full one.
/// Split backups use <c>{name}.partNNNN</c> volumes and a <c>{name}.manifest.json</c> file.
/// </summary>
public static partial class BackupNaming
{
    public const string PartialSuffix = ".partial";
    private const string TimestampFormat = "yyyyMMdd_HHmmss";

    public const string IncrementalMarker = ".inc";

    public static string CreateFileName(string prefix, DateTimeOffset timestampUtc, bool encrypted, bool zstd = false, bool incremental = false) =>
        $"{prefix}_{timestampUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}{(incremental ? IncrementalMarker : string.Empty)}.zip{(zstd ? ArchiveBuilder.ZstdExtension : string.Empty)}{(encrypted ? AesFileEncryptor.FileExtension : string.Empty)}";

    /// <summary>
    /// Groups remote file names into backups that belong to <paramref name="prefix"/>. A backup is either a
    /// single archive or a complete set of volumes (the manifest exists). Incomplete volume sets are ignored.
    /// </summary>
    public static IReadOnlyList<BackupFileInfo> ParseBackups(string prefix, IEnumerable<string> remoteNames)
    {
        var names = remoteNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, BackupFileInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            string archive;
            if (name.EndsWith(ChunkManifest.Extension, StringComparison.OrdinalIgnoreCase))
            {
                archive = name[..^ChunkManifest.Extension.Length];
            }
            else
            {
                archive = name;
            }

            if (result.ContainsKey(archive) || !TryParseArchive(prefix, archive, out var createdAt))
            {
                continue;
            }

            var related = new List<string>();
            if (names.Contains(archive))
            {
                related.Add(archive);
            }

            if (names.Contains(archive + ChunkManifest.Extension))
            {
                related.Add(archive + ChunkManifest.Extension);
                related.AddRange(PartsOf(archive, names));
            }

            if (names.Contains(archive + Checksum.SidecarExtension))
            {
                related.Add(archive + Checksum.SidecarExtension);
            }

            if (names.Contains(archive + BackupIndex.Extension))
            {
                related.Add(archive + BackupIndex.Extension);
            }

            result[archive] = new BackupFileInfo(archive, createdAt, related);
        }

        return result.Values.ToList();
    }

    /// <summary>Volumes of <paramref name="archive"/> present in <paramref name="names"/>.</summary>
    public static IEnumerable<string> PartsOf(string archive, IEnumerable<string> names) =>
        names.Where(n => n.StartsWith(archive + ".part", StringComparison.OrdinalIgnoreCase) && PartSuffix().IsMatch(n[archive.Length..]))
             .Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Volumes and checksums of this job that do not belong to a complete backup (e.g. an interrupted upload).
    /// </summary>
    public static IEnumerable<string> OrphanedFiles(string prefix, IEnumerable<string> remoteNames, string? keep)
    {
        var names = remoteNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var match = PartName().Match(name);
            if (!match.Success)
            {
                continue;
            }

            var archive = match.Groups["archive"].Value;
            if (!string.Equals(archive, keep, StringComparison.OrdinalIgnoreCase)
                && TryParseArchive(prefix, archive, out _)
                && !names.Contains(archive + ChunkManifest.Extension))
            {
                yield return name;
            }
        }
    }

    /// <summary>True for incremental backups (<c>.inc.zip</c>), which depend on earlier backups.</summary>
    public static bool IsIncremental(string name) => name.Contains(IncrementalMarker + ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>The job prefix of a backup file name, or null when the name does not follow the convention.</summary>
    public static string? PrefixOf(string name)
    {
        var match = ArchiveName().Match(name);
        return match.Success ? match.Groups["prefix"].Value : null;
    }

    /// <summary>
    /// Backups needed to restore <paramref name="target"/>, oldest first: the full backup it is based on and the
    /// incremental backups up to it. A full backup is its own chain.
    /// </summary>
    public static IReadOnlyList<BackupFileInfo> ChainOf(IReadOnlyList<BackupFileInfo> backups, string target)
    {
        var ordered = backups.OrderBy(b => b.CreatedAt).ToList();
        var index = ordered.FindIndex(b => string.Equals(b.Name, target, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new FileNotFoundException($"Backup '{target}' was not found.");
        }

        var start = index;
        while (IsIncremental(ordered[start].Name))
        {
            if (start == 0)
            {
                throw new InvalidDataException($"The full backup that '{target}' is based on is missing.");
            }

            start--;
        }

        return ordered.GetRange(start, index - start + 1);
    }

    public static bool TryParseArchive(string prefix, string name, out DateTimeOffset createdAt)
    {
        createdAt = default;
        var start = prefix + "_";
        if (!name.StartsWith(start, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = name[start.Length..];
        if (rest.EndsWith(AesFileEncryptor.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[..^AesFileEncryptor.FileExtension.Length];
        }

        if (rest.EndsWith(ArchiveBuilder.ZstdExtension, StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[..^ArchiveBuilder.ZstdExtension.Length];
        }

        if (!rest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stamp = rest.Split('.')[0];
        if (!DateTime.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        createdAt = new DateTimeOffset(parsed, TimeSpan.Zero);
        return true;
    }

    [GeneratedRegex(@"^(?<prefix>.+)_\d{8}_\d{6}(\.inc)?\.zip(\.zst)?(\.aes)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveName();

    [GeneratedRegex(@"^\.part\d{4,}$", RegexOptions.IgnoreCase)]
    private static partial Regex PartSuffix();

    [GeneratedRegex(@"^(?<archive>.+)\.part\d{4,}$", RegexOptions.IgnoreCase)]
    private static partial Regex PartName();
}
