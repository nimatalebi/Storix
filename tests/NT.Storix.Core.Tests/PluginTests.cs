using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Configuration;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Plugins;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

public class PluginTests
{
    private sealed class MarkingProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText is null ? null : "enc:" + plainText;

        public string? Unprotect(string? protectedText) => protectedText?.StartsWith("enc:", StringComparison.Ordinal) == true ? protectedText[4..] : protectedText;
    }

    // Loaded once per test run. The folder is not deleted: Windows keeps a loaded plugin DLL locked.
    private static readonly Lazy<int> Sample = new(() =>
    {
        var plugins = Path.Combine(Path.GetTempPath(), "storix-plugin-tests", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(plugins, "NT.Storix.Plugins.Sample");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "sample-plugin", "NT.Storix.Plugins.Sample.dll"), Path.Combine(folder, "NT.Storix.Plugins.Sample.dll"));
        return PluginRegistry.LoadFrom(plugins);
    });

    private static int LoadSample() => Sample.Value;

    [Fact]
    public async Task Plugins_loaded_from_disk_back_up_and_restore()
    {
        Assert.Equal(2, LoadSample());
        Assert.Contains(PluginRegistry.Sources, p => p.Id == "sample.text");
        Assert.Contains(PluginRegistry.Destinations, p => p.Id == "sample.folder");

        using var temp = new TempDirectory();
        var database = new StorixDatabase(temp.Combine("s.db"));
        var settings = new SettingsRepository(database, new MarkingProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        var job = new BackupJob
        {
            Name = "Plugin job",
            Source = { Kind = SourceKind.Plugin, Plugin = { Plugin = "sample.text", SettingsText = "FileName=inventory.txt\nContent=42 servers" } },
            Destinations =
            [
                new DestinationDefinition
                {
                    Name = "Plugin folder",
                    Kind = DestinationKind.Plugin,
                    Plugin = { Plugin = "sample.folder", SettingsText = $"Path={temp.Combine("dst")}", SecretsText = "Token=abc" },
                },
            ],
        };
        Assert.Empty(BackupJobRunner.GetValidationErrors(job));

        var runner = new BackupJobRunner(new RunRepository(database), settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
        var run = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Contains("Sample plugin wrote inventory.txt", run.Log);
        var restore = new RestoreService(new DestinationFactory());
        await restore.RestoreFromDestinationAsync(job.Destinations[0], run.FileName!, new RestoreRequest(temp.Combine("r")), null, CancellationToken.None);
        Assert.Equal("42 servers", File.ReadAllText(temp.Combine("r", "sample", "inventory.txt")));
    }

    [Fact]
    public void Plugin_secrets_are_protected_in_the_database_and_removed_from_exports()
    {
        using var temp = new TempDirectory();
        var jobs = new JobRepository(new StorixDatabase(temp.Combine("s.db")), new MarkingProtector());
        var job = new BackupJob
        {
            Name = "Secrets",
            Destinations = [new DestinationDefinition { Kind = DestinationKind.Plugin, Plugin = { Plugin = "x", SettingsText = "Path=/a", SecretsText = "Token=abc; Key=def" } }],
        };

        jobs.Save(job);
        string stored;
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.Combine("s.db")};Pooling=False"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "SELECT definition FROM jobs";
            stored = (string)command.ExecuteScalar()!;
        }

        Assert.Contains("enc:abc", stored);
        Assert.DoesNotContain("\"abc\"", stored);
        var loaded = jobs.Get(job.Id)!.Destinations[0].Plugin;
        Assert.Equal("abc", loaded.Secrets["Token"]);
        Assert.Equal("/a", loaded.Settings["Path"]);
        Assert.Equal("abc", loaded.ToContext().Get("token"));

        var export = ConfigurationPorter.Export([jobs.Get(job.Id)!], null, passphrase: null);
        Assert.DoesNotContain("abc", export);
        Assert.DoesNotContain("def", export);
    }

    [Fact]
    public void Missing_plugins_and_required_settings_are_reported()
    {
        LoadSample();
        var job = new BackupJob
        {
            Source = { Kind = SourceKind.Plugin, Plugin = { Plugin = "sample.text" } },
            Destinations = [new DestinationDefinition { Name = "D", Kind = DestinationKind.Plugin, Plugin = { Plugin = "nope" } }],
        };

        var errors = BackupJobRunner.GetValidationErrors(job);

        Assert.Contains(errors, e => e.Contains("'FileName' is required"));
        Assert.Contains(errors, e => e.Contains("D: The plugin 'nope' is not installed"));
    }
}
