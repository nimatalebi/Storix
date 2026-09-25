namespace NT.Storix.Core;

/// <summary>Well-known locations shared by the service, the CLI and the manager UI.</summary>
public static class StorixPaths
{
    public const string ServiceName = "Storix";

    /// <summary>Overrides the data folder (useful for development and tests).</summary>
    public const string DataDirectoryEnvironmentVariable = "STORIX_DATA_DIR";

    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden;
            }

            // Linux: the usual place for service state (CommonApplicationData would be /usr/share).
            if (OperatingSystem.IsLinux())
            {
                return "/var/lib/storix";
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.Create),
                "Storix");
        }
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "storix.db");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string DefaultStagingDirectory => Path.Combine(DataDirectory, "staging");
}
