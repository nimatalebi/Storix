using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

public class SqliteSourceTests
{
    [Fact]
    public async Task Online_backup_copies_a_database_that_is_in_use()
    {
        using var temp = new TempDirectory();
        var db = temp.Combine("app.db");
        await using var open = new SqliteConnection($"Data Source={db};Pooling=false");
        await open.OpenAsync();
        await using (var command = open.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t (v) VALUES ('a'), ('b'), ('c');";
            await command.ExecuteNonQueryAsync();
        }

        Directory.CreateDirectory(temp.Combine("staging"));
        var snapshot = await new SqliteSource(new SqliteSourceOptions { DatabasePaths = db })
            .PrepareAsync(new SourceContext(temp.Combine("staging"), new RunLog(NullLogger.Instance, "sqlite")), CancellationToken.None);

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal("sqlite/app.db", entry.EntryName);
        await using var copy = new SqliteConnection($"Data Source={entry.SourcePath};Mode=ReadOnly;Pooling=false");
        await copy.OpenAsync();
        await using var count = copy.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM t";
        Assert.Equal(3L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Windows_system_source_requires_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
            new WindowsSystemSource(new WindowsSystemSourceOptions()).PrepareAsync(new SourceContext(Path.GetTempPath(), new RunLog(NullLogger.Instance, "x")), CancellationToken.None));
    }

    [WindowsAdminFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Windows_system_source_exports_registry_and_certificates()
    {
        using var temp = new TempDirectory();
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\StorixTest"))
        {
            key.SetValue("Answer", 42);
        }

        try
        {
            Directory.CreateDirectory(temp.Combine("staging"));
            var snapshot = await new WindowsSystemSource(new WindowsSystemSourceOptions
            {
                IisConfiguration = false,
                ScheduledTasks = true,
                RegistryKeys = @"HKCU\Software\StorixTest",
                CertificateStores = "Root",
            }).PrepareAsync(new SourceContext(temp.Combine("staging"), new RunLog(NullLogger.Instance, "system")), CancellationToken.None);

            var reg = snapshot.Entries.Single(e => e.EntryName.StartsWith("system/registry/"));
            Assert.Contains("Answer", File.ReadAllText(reg.SourcePath));
            Assert.Contains(snapshot.Entries, e => e.EntryName.StartsWith("system/certificates/root/") && e.EntryName.EndsWith(".cer"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\StorixTest", throwOnMissingSubKey: false);
        }
    }
}
