using System.Security.Cryptography;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Security;

namespace NT.Storix.Core.Tests;

public class KeyManagementTests
{
    [Fact]
    public void Password_only_secret_is_unchanged_for_backward_compatibility()
    {
        Assert.Equal("pw", EncryptionSecret.Combine("pw", null));
        Assert.Null(EncryptionSecret.Combine(null, " "));
    }

    [Fact]
    public void Key_file_changes_the_secret_and_must_exist()
    {
        using var temp = new TempDirectory();
        var key = temp.Combine("backup.key");
        EncryptionSecret.CreateKeyFile(key);

        var withKey = EncryptionSecret.Combine("pw", key);
        Assert.NotEqual("pw", withKey);
        Assert.Equal(withKey, EncryptionSecret.Combine("pw", key));
        Assert.NotEqual(withKey, EncryptionSecret.Combine(null, key));

        EncryptionSecret.CreateKeyFile(temp.Combine("other.key"));
        Assert.NotEqual(withKey, EncryptionSecret.Combine("pw", temp.Combine("other.key")));
        Assert.Throws<FileNotFoundException>(() => EncryptionSecret.Combine("pw", temp.Combine("missing.key")));
    }

    [Fact]
    public async Task Backup_with_key_file_needs_the_key_file_to_restore()
    {
        using var temp = new TempDirectory();
        var key = temp.Combine("backup.key");
        EncryptionSecret.CreateKeyFile(key);
        temp.WriteFile("source/a.txt", "secret content");

        var job = new BackupJob
        {
            Name = "Key file",
            Source = { Files = { Paths = [temp.Combine("source")] } },
            Processing = { Encrypt = true, EncryptionPassword = null, EncryptionKeyFile = key },
            Destinations = [new DestinationDefinition { LocalFolder = { Path = temp.Combine("dst") } }],
        };
        Assert.Empty(BackupJobRunner.GetValidationErrors(job));

        var (_, run) = await RunAsync(temp, job);
        var archive = temp.Combine("dst", run.FileName!);

        await Assert.ThrowsAsync<CryptographicException>(() =>
            RestoreService.RestoreFromFileAsync(archive, new RestoreRequest(temp.Combine("r1"), "wrong"), null, CancellationToken.None));

        await RestoreService.RestoreFromFileAsync(archive, new RestoreRequest(temp.Combine("r2"), EncryptionSecret.Combine(null, key)), null, CancellationToken.None);
        Assert.Equal("secret content", File.ReadAllText(temp.Combine("r2", "source", "a.txt")));
    }

    [Fact]
    public void Recovery_sheet_contains_what_is_needed_and_hides_password_on_request()
    {
        var job = new BackupJob { Name = "Payroll <DB>", Processing = { Encrypt = true, EncryptionPassword = "Tr0ub4dor&3" } };

        var withPassword = RecoverySheet.BuildHtml(job, includePassword: true);
        Assert.Contains("Tr0ub4dor&amp;3", withPassword);
        Assert.Contains("Payroll &lt;DB&gt;", withPassword);
        Assert.Contains(job.Id.ToString(), withPassword);

        Assert.DoesNotContain("Tr0ub4dor", RecoverySheet.BuildHtml(job, includePassword: false));
    }

    private static async Task<(BackupJob, BackupRun)> RunAsync(TempDirectory temp, BackupJob job)
    {
        var database = new Persistence.StorixDatabase(temp.Combine("s.db"));
        var settings = new Persistence.SettingsRepository(database, new MachineSecretProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        var runner = new BackupJobRunner(new Persistence.RunRepository(database), settings, new Sources.SourceFactory(), new Destinations.DestinationFactory(),
            Array.Empty<Monitoring.INotifier>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupJobRunner>.Instance);
        var run = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        return (job, run);
    }
}
