using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Engine;

/// <summary>Early free-space checks so a run fails fast instead of filling the disk.</summary>
public static class FreeSpace
{
    /// <summary>Safety margin applied to estimates (25% growth).</summary>
    public const double GrowthFactor = 1.25;

    /// <summary>
    /// Estimates the staging space needed for the archive: based on the previous backup size when known,
    /// otherwise on the uncompressed size of the source files. Encryption briefly needs the space twice.
    /// </summary>
    public static long EstimateStagingBytes(IEnumerable<ArchiveEntry> entries, long? previousBackupSize, bool encrypt)
    {
        long estimate;
        if (previousBackupSize is > 0)
        {
            estimate = (long)(previousBackupSize.Value * GrowthFactor);
        }
        else
        {
            estimate = 0;
            foreach (var entry in entries)
            {
                try
                {
                    estimate += new FileInfo(entry.SourcePath).Length;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return encrypt ? estimate * 2 : estimate;
    }

    /// <summary>Free bytes on the volume holding <paramref name="path"/>, or null when unknown (e.g. some UNC paths).</summary>
    public static long? GetAvailableBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <exception cref="IOException">Not enough free space.</exception>
    public static void Ensure(string path, long requiredBytes, string what)
    {
        if (requiredBytes <= 0 || GetAvailableBytes(path) is not { } available || available >= requiredBytes)
        {
            return;
        }

        throw new IOException(
            $"Not enough free space for {what} on '{Path.GetPathRoot(Path.GetFullPath(path))}': about {BackupJobRunner.FormatSize(requiredBytes)} needed, " +
            $"{BackupJobRunner.FormatSize(available)} available.");
    }
}
