using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

public class RestoreDrillTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<Notification> Sent { get; } = [];

        public Task NotifyAsync(Notification notification, CancellationToken cancellationToken)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class Setup
    {
        public Setup(TempDirectory temp)
        {
            var database = new StorixDatabase(temp.Combine("s.db"));
            Settings = new SettingsRepository(database, new PlainProtector());
            Settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
            Runs = new RunRepository(database);
            Jobs = new JobRepository(database, new PlainProtector());
            Runner = new BackupJobRunner(Runs, Settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
            Drills = new RestoreDrillRunner(Runs, Settings, new DestinationFactory(), [Notifier], NullLogger<RestoreDrillRunner>.Instance);
            temp.WriteFile("src/a.txt", "drill me");
            Job = new BackupJob
            {
                Name = "Drill",
                Source = { Files = { Paths = [temp.Combine("src")] } },
                Processing = { Encrypt = true, EncryptionPassword = "pw" },
                Destinations = [new DestinationDefinition { LocalFolder = { Path = temp.Combine("dst") } }],
                RestoreDrill = { Enabled = true, EveryDays = 7 },
                Notifications = { OnFailure = true },
            };
            Jobs.Save(Job);
        }

        public SettingsRepository Settings { get; }

        public RunRepository Runs { get; }

        public JobRepository Jobs { get; }

        public BackupJobRunner Runner { get; }

        public RestoreDrillRunner Drills { get; }

        public RecordingNotifier Notifier { get; } = new();

        public BackupJob Job { get; }
    }

    [Fact]
    public async Task Drill_restores_latest_backup_and_records_success()
    {
        using var temp = new TempDirectory();
        var setup = new Setup(temp);
        var backup = await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, CancellationToken.None);

        var drill = await setup.Drills.RunAsync(setup.Job, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, drill.Status);
        Assert.Equal(RunTrigger.RestoreDrill, drill.Trigger);
        Assert.Equal(backup.FileName, drill.FileName);
        Assert.Contains("checksum verified", drill.Log);
        Assert.Empty(Directory.GetDirectories(temp.Combine("staging")));
        Assert.NotNull(setup.Runs.GetLastDrill(setup.Job.Id));
    }

    [Fact]
    public async Task Drill_fails_loudly_on_corrupted_backup_and_notifies()
    {
        using var temp = new TempDirectory();
        var setup = new Setup(temp);
        var backup = await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, CancellationToken.None);
        var archive = temp.Combine("dst", backup.FileName!);
        var bytes = await File.ReadAllBytesAsync(archive);
        bytes[^40] ^= 0xFF;
        await File.WriteAllBytesAsync(archive, bytes);

        var drill = await setup.Drills.RunAsync(setup.Job, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, drill.Status);
        Assert.Contains("Checksum mismatch", drill.Message);
        Assert.Contains("restore drill Failed", Assert.Single(setup.Notifier.Sent).Title);
    }

    [Fact]
    public async Task Drill_is_due_only_after_a_successful_backup_and_the_interval()
    {
        using var temp = new TempDirectory();
        var setup = new Setup(temp);
        var scheduler = new BackupScheduler(setup.Jobs, setup.Runs, setup.Settings, setup.Runner, Array.Empty<INotifier>(), NullLogger<BackupScheduler>.Instance, setup.Drills);

        Assert.False(scheduler.IsDrillDue(setup.Job, DateTimeOffset.UtcNow)); // No backup yet.

        await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, CancellationToken.None);
        Assert.True(scheduler.IsDrillDue(setup.Job, DateTimeOffset.UtcNow));

        await setup.Drills.RunAsync(setup.Job, CancellationToken.None);
        Assert.False(scheduler.IsDrillDue(setup.Job, DateTimeOffset.UtcNow));
        Assert.True(scheduler.IsDrillDue(setup.Job, DateTimeOffset.UtcNow.AddDays(8)));

        setup.Job.RestoreDrill.Enabled = false;
        Assert.False(scheduler.IsDrillDue(setup.Job, DateTimeOffset.UtcNow.AddDays(8)));
    }
}
