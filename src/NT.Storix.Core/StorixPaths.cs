namespace NT.Storix.Core;

/// <summary>Well-known locations shared by the Windows service and the manager UI.</summary>
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

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.Create),
                "Storix");
        }
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "storix.db");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string DefaultStagingDirectory => Path.Combine(DataDirectory, "staging");
}
