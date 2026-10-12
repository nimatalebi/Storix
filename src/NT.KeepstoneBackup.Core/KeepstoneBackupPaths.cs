namespace NT.KeepstoneBackup.Core;

/// <summary>Well-known locations shared by the service, the CLI and the manager UI.</summary>
public static class KeepstoneBackupPaths
{
    public const string ServiceName = "KeepstoneBackup";

    /// <summary>Overrides the data folder (useful for development and tests).</summary>
    public const string DataDirectoryEnvironmentVariable = "KEEPSTONEBACKUP_DATA_DIR";

    // The project was called Storix before; installations from that time keep their data where it is.
    private const string LegacyDataDirectoryEnvironmentVariable = "STORIX_DATA_DIR";
    private const string DatabaseFileName = "keepstonebackup.db";
    private const string LegacyDatabaseFileName = "storix.db";

    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable)
                             ?? Environment.GetEnvironmentVariable(LegacyDataDirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden;
            }

            var directory = DefaultDataDirectory("keepstonebackup", "KeepstoneBackup");
            var legacy = DefaultDataDirectory("storix", "Storix");
            return !File.Exists(Path.Combine(directory, DatabaseFileName)) && Directory.Exists(legacy) ? legacy : directory;
        }
    }

    public static string DatabasePath
    {
        get
        {
            var directory = DataDirectory;
            var path = Path.Combine(directory, DatabaseFileName);
            var legacy = Path.Combine(directory, LegacyDatabaseFileName);
            return !File.Exists(path) && File.Exists(legacy) ? legacy : path;
        }
    }

    private static string DefaultDataDirectory(string linuxName, string windowsName)
    {
        // Linux: the usual place for service state (CommonApplicationData would be /usr/share).
        if (OperatingSystem.IsLinux())
        {
            return "/var/lib/" + linuxName;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.Create),
            windowsName);
    }

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string DefaultStagingDirectory => Path.Combine(DataDirectory, "staging");
}
