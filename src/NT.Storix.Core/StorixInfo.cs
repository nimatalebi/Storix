using System.Reflection;
using System.Runtime.InteropServices;

namespace NT.Storix.Core;

/// <summary>Public project information shown in the About and Feedback pages.</summary>
public static class StorixInfo
{
    public const string ProductName = "Storix";
    public const string Authors = "Storix Contributors";
    public const string License = "MIT";
    public const string RepositoryUrl = "https://github.com/nimatalebi/Storix";
    public const string IssuesUrl = RepositoryUrl + "/issues";
    public const string NewIssueUrl = IssuesUrl + "/new";
    public const string LicenseUrl = RepositoryUrl + "/blob/main/LICENSE";
    public const string ContactEmail = "nimatweb@gmail.com";

    public static string Version =>
        typeof(StorixInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(StorixInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>Non-sensitive environment details that help to reproduce a problem.</summary>
    public static string SystemSummary =>
        $"Storix {Version} | {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) | {RuntimeInformation.FrameworkDescription}";
}
