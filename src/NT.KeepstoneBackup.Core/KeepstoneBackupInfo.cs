using System.Reflection;
using System.Runtime.InteropServices;

namespace NT.KeepstoneBackup.Core;

/// <summary>Public project information shown in the About and Feedback pages.</summary>
public static class KeepstoneBackupInfo
{
    public const string ProductName = "KeepstoneBackup";
    public const string Authors = "KeepstoneBackup Contributors";
    public const string License = "MIT";
    public const string RepositoryUrl = "https://github.com/nimatalebi/Storix";
    public const string IssuesUrl = RepositoryUrl + "/issues";
    public const string NewIssueUrl = IssuesUrl + "/new";
    public const string LicenseUrl = RepositoryUrl + "/blob/main/LICENSE";
    public const string ContactEmail = "nimatweb@gmail.com";

    public static string Version =>
        typeof(KeepstoneBackupInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(KeepstoneBackupInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>Non-sensitive environment details that help to reproduce a problem.</summary>
    public static string SystemSummary =>
        $"KeepstoneBackup {Version} | {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) | {RuntimeInformation.FrameworkDescription}";
}
