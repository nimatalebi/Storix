using System.Globalization;
using NT.Storix.Core.Destinations;

namespace NT.Storix.Core.Processing;

/// <summary>Naming convention: <c>{prefix}_{yyyyMMdd_HHmmss}.zip[.aes]</c> (timestamp in UTC).</summary>
public static class BackupNaming
{
    public const string PartialSuffix = ".partial";
    private const string TimestampFormat = "yyyyMMdd_HHmmss";

    public static string CreateFileName(string prefix, DateTimeOffset timestampUtc, bool encrypted) =>
        $"{prefix}_{timestampUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}.zip{(encrypted ? AesFileEncryptor.FileExtension : string.Empty)}";

    /// <summary>Groups remote file names into backups (archive + sidecar) that belong to <paramref name="prefix"/>.</summary>
    public static IReadOnlyList<BackupFileInfo> ParseBackups(string prefix, IEnumerable<string> remoteNames)
    {
        var names = remoteNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<BackupFileInfo>();

        foreach (var name in names)
        {
            if (!TryParseArchive(prefix, name, out var createdAt))
            {
                continue;
            }

            var related = new List<string> { name };
            var sidecar = name + Checksum.SidecarExtension;
            if (names.Contains(sidecar))
            {
                related.Add(sidecar);
            }

            result.Add(new BackupFileInfo(name, createdAt, related));
        }

        return result;
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
}
