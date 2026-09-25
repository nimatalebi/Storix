using System.Security.Principal;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace NT.Storix.Core.Tests;

/// <summary>Runs only on Windows as administrator (e.g. the Windows CI runner).</summary>
public sealed class WindowsAdminFactAttribute : FactAttribute
{
    public WindowsAdminFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only.";
        }
        else if (!IsAdministrator())
        {
            Skip = "Requires administrator rights.";
        }
    }

    private static bool IsAdministrator() =>
        OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}

public class VssTests
{
    [WindowsAdminFact]
    public async Task Locked_file_is_backed_up_through_a_shadow_copy()
    {
        using var temp = new TempDirectory();
        var locked = temp.WriteFile("data/locked.db", "consistent content");

        // Hold an exclusive lock, like a database engine would.
        await using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var source = new FileSource(new FileSourceOptions { Paths = [temp.Combine("data")], UseVss = true, SkipLockedFiles = false });
        var log = new RunLog(NullLogger.Instance, "vss");
        var snapshot = await source.PrepareAsync(new SourceContext(temp.Path, log), CancellationToken.None);
        try
        {
            Assert.Contains("Created a shadow copy", log.ToString());
            var zip = temp.Combine("out.zip");
            await ArchiveBuilder.CreateAsync(snapshot.Entries, zip, ArchiveCompression.Fastest, null, CancellationToken.None);

            await RestoreService.RestoreFromFileAsync(zip, new RestoreRequest(temp.Combine("restored")), null, CancellationToken.None);
            Assert.Equal("consistent content", File.ReadAllText(temp.Combine("restored", "data", "locked.db")));
        }
        finally
        {
            foreach (var resource in snapshot.Resources)
            {
                resource.Dispose();
            }
        }
    }

    [Fact]
    public async Task Vss_on_other_platforms_falls_back_to_live_files()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        temp.WriteFile("data/a.txt", "a");
        var log = new RunLog(NullLogger.Instance, "vss");
        var snapshot = await new FileSource(new FileSourceOptions { Paths = [temp.Combine("data")], UseVss = true })
            .PrepareAsync(new SourceContext(temp.Path, log), CancellationToken.None);

        Assert.Single(snapshot.Entries);
        Assert.Empty(snapshot.Resources);
        Assert.Contains("only available on Windows", log.ToString());
    }
}
