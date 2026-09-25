using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

public class CopyJobTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    private sealed class Harness
    {
        public Harness(TempDirectory temp)
        {
            var database = new StorixDatabase(temp.Combine("storix.db"));
            var settings = new SettingsRepository(database, new PlainProtector());
            settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
            Jobs = new JobRepository(database, new PlainProtector());
            Runs = new RunRepository(database);
            Runner = new BackupJobRunner(Runs, settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance, jobs: Jobs);
        }

        public JobRepository Jobs { get; }

        public RunRepository Runs { get; }

        public BackupJobRunner Runner { get; }
    }

    private static BackupJob SourceJob(TempDirectory temp, int splitMb = 0) => new()
    {
        Name = "Web site",
        Source = { Files = { Paths = [temp.Combine("src")] } },
        Processing = { Encrypt = true, EncryptionPassword = "pw", SplitSizeMb = splitMb },
        Retention = { KeepLast = 10 },
        Destinations = [new DestinationDefinition { Name = "NAS", LocalFolder = { Path = temp.Combine("nas") } }],
    };

    private static BackupJob CopyJob(TempDirectory temp, int keepLast = 10) => new()
    {
        Name = "Offsite copy",
        Source = { Kind = SourceKind.CopyOf, CopyOf = { Job = "web site" } },
        Retention = { KeepLast = keepLast },
        Destinations = [new DestinationDefinition { Name = "Offsite", LocalFolder = { Path = temp.Combine("offsite") } }],
    };

    [Fact]
    public async Task Copies_missing_backups_once_and_they_restore_with_the_original_password()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("src/a.txt", "first");
        var h = new Harness(temp);
        var source = SourceJob(temp);
        h.Jobs.Save(source);
        var backup = await h.Runner.RunAsync(source, RunTrigger.Manual, CancellationToken.None);
        var copy = CopyJob(temp);
        h.Jobs.Save(copy);

        var first = await h.Runner.RunAsync(copy, RunTrigger.Manual, CancellationToken.None);
        var second = await h.Runner.RunAsync(copy, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, first.Status);
        Assert.StartsWith("1 backup copy", first.Message);
        Assert.StartsWith("0 backup copies", second.Message);
        Assert.Equal(backup.FileName, first.FileName);
        Assert.Equal(
            Directory.GetFiles(temp.Combine("nas")).Select(Path.GetFileName).Order(),
            Directory.GetFiles(temp.Combine("offsite")).Select(Path.GetFileName).Order());

        await RestoreService.RestoreFromFileAsync(temp.Combine("offsite", backup.FileName!), new RestoreRequest(temp.Combine("restored"), "pw"), null, CancellationToken.None);
        Assert.Equal("first", File.ReadAllText(temp.Combine("restored", "src", "a.txt")));
    }

    [Fact]
    public async Task Copy_retention_keeps_only_the_newest_backups()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("src/a.txt", "a");
        var h = new Harness(temp);
        var source = SourceJob(temp);
        h.Jobs.Save(source);
        var names = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            names.Add((await h.Runner.RunAsync(source, RunTrigger.Manual, CancellationToken.None)).FileName!);
            await Task.Delay(1100); // Backup names have a one-second resolution.
        }

        var copy = CopyJob(temp, keepLast: 2);
        h.Jobs.Save(copy);
        var run = await h.Runner.RunAsync(copy, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        var copied = BackupNaming.ParseBackups(source.FilePrefix, Directory.GetFiles(temp.Combine("offsite")).Select(f => Path.GetFileName(f)!));
        Assert.Equal(names.Skip(1).Order(), copied.Select(b => b.Name).Order());
        Assert.Equal(3, Directory.GetFiles(temp.Combine("nas"), "*.aes").Length);
    }

    [Fact]
    public async Task Split_backups_are_copied_as_complete_volume_sets()
    {
        using var temp = new TempDirectory();
        await File.WriteAllBytesAsync(temp.Combine("big.bin"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(3 * 1024 * 1024));
        Directory.CreateDirectory(temp.Combine("src"));
        File.Move(temp.Combine("big.bin"), temp.Combine("src", "big.bin"));
        var h = new Harness(temp);
        var source = SourceJob(temp, splitMb: 1);
        source.Processing.Compression = ArchiveCompression.None;
        h.Jobs.Save(source);
        var backup = await h.Runner.RunAsync(source, RunTrigger.Manual, CancellationToken.None);
        var copy = CopyJob(temp);
        h.Jobs.Save(copy);

        var run = await h.Runner.RunAsync(copy, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.True(Directory.GetFiles(temp.Combine("offsite"), "*.part*").Length >= 3);
        await RestoreService.RestoreFromFileAsync(temp.Combine("offsite", backup.FileName + ChunkManifest.Extension), new RestoreRequest(temp.Combine("restored"), "pw"), null, CancellationToken.None);
        Assert.Equal(3 * 1024 * 1024, new FileInfo(temp.Combine("restored", "src", "big.bin")).Length);
    }

    [Fact]
    public async Task Corrupted_source_backup_is_not_copied()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("src/a.txt", "a");
        var h = new Harness(temp);
        var source = SourceJob(temp);
        h.Jobs.Save(source);
        var backup = await h.Runner.RunAsync(source, RunTrigger.Manual, CancellationToken.None);
        var archive = temp.Combine("nas", backup.FileName!);
        var bytes = File.ReadAllBytes(archive);
        bytes[^40] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);
        var copy = CopyJob(temp);
        h.Jobs.Save(copy);

        var run = await h.Runner.RunAsync(copy, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("checksum", run.Message);
        Assert.False(File.Exists(temp.Combine("offsite", backup.FileName!)));
    }

    [Fact]
    public async Task Unknown_source_job_fails_clearly()
    {
        using var temp = new TempDirectory();
        var h = new Harness(temp);
        var copy = CopyJob(temp);
        copy.Source.CopyOf.Job = "missing";

        var run = await h.Runner.RunAsync(copy, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("does not exist", run.Message);
        Assert.Contains(BackupJobRunner.GetValidationErrors(new BackupJob { Source = { Kind = SourceKind.CopyOf }, Destinations = copy.Destinations }), e => e.Contains("Choose the job"));
    }
}
