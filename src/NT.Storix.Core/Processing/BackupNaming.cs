using System.Globalization;
using System.Text.RegularExpressions;
using NT.Storix.Core.Destinations;

namespace NT.Storix.Core.Processing;

/// <summary>
/// Naming convention: <c>{prefix}_{yyyyMMdd_HHmmss}.zip[.aes]</c> (timestamp in UTC), plus <c>.sha256</c> sidecars.
/// Split backups use <c>{name}.partNNNN</c> volumes and a <c>{name}.manifest.json</c> file.
/// </summary>
public static partial class BackupNaming
{
    public const string PartialSuffix = ".partial";
    private const string TimestampFormat = "yyyyMMdd_HHmmss";

    public static string CreateFileName(string prefix, DateTimeOffset timestampUtc, bool encrypted) =>
        $"{prefix}_{timestampUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}.zip{(encrypted ? AesFileEncryptor.FileExtension : string.Empty)}";

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

    public static bool TryParseArchive(string prefix, string name, out DateTimeOffset createdAt)
    {
        createdAt = default;
        var start = prefix + "_";
        if (!name.StartsWith(start, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = name[start.Length..];
        if (!(rest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || rest.EndsWith(".zip" + AesFileEncryptor.FileExtension, StringComparison.OrdinalIgnoreCase)))
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

    [GeneratedRegex(@"^\.part\d{4,}$", RegexOptions.IgnoreCase)]
    private static partial Regex PartSuffix();

    [GeneratedRegex(@"^(?<archive>.+)\.part\d{4,}$", RegexOptions.IgnoreCase)]
    private static partial Regex PartName();
}
