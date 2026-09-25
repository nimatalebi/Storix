using System.Security.Cryptography;
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

public class FaultInjectionTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    /// <summary>Wraps a local folder and lets a test inject failures into uploads.</summary>
    private sealed class FaultyDestinationFactory(Func<LocalFolderDestination, string, string, CancellationToken, Task>? upload) : IDestinationFactory
    {
        public int Uploads;

        public Func<LocalFolderDestination, string, string, CancellationToken, Task>? Upload { get; } = upload;

        public IBackupDestination Create(DestinationDefinition definition) => new Faulty(new LocalFolderDestination(definition.LocalFolder), this);

        private sealed class Faulty(LocalFolderDestination inner, FaultyDestinationFactory owner) : IBackupDestination
        {
            public Task TestAsync(CancellationToken cancellationToken) => inner.TestAsync(cancellationToken);

            public Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

            public Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.Uploads);
                return owner.Upload is null ? inner.UploadAsync(localPath, remoteName, progress, cancellationToken) : owner.Upload(inner, localPath, remoteName, cancellationToken);
            }

            public Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken) =>
                inner.DownloadAsync(remoteName, localPath, progress, cancellationToken);

            public Task DeleteAsync(string remoteName, CancellationToken cancellationToken) => inner.DeleteAsync(remoteName, cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Setup
    {
        public Setup(TempDirectory temp, IDestinationFactory factory)
        {
            var database = new StorixDatabase(temp.Combine("storix.db"));
            Settings = new SettingsRepository(database, new PlainProtector());
            Settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
            Runs = new RunRepository(database);
            Jobs = new JobRepository(database, new PlainProtector());
            Runner = new BackupJobRunner(Runs, Settings, new SourceFactory(), factory, Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);

            Directory.CreateDirectory(temp.Combine("source"));
            File.WriteAllBytes(temp.Combine("source", "data.bin"), RandomNumberGenerator.GetBytes(3 * 1024 * 1024));
            Job = new BackupJob
            {
                Name = "Faults",
                Source = { Files = { Paths = [temp.Combine("source")] } },
                Processing = { Encrypt = true, EncryptionPassword = "pw" },
                Destinations = [new DestinationDefinition { Name = "Local", LocalFolder = { Path = temp.Combine("target") } }],
                Retry = { MaxAttempts = 3, InitialDelaySeconds = 0 },
            };
        }

        public SettingsRepository Settings { get; }

        public RunRepository Runs { get; }

        public JobRepository Jobs { get; }

        public BackupJobRunner Runner { get; }

        public BackupJob Job { get; }
    }

    [Fact]
    public async Task Network_drop_mid_upload_resumes_from_partial_file()
    {
        using var temp = new TempDirectory();
        long resumedFrom = -1;
        var first = true;

        var factory = new FaultyDestinationFactory(async (inner, local, remote, ct) =>
        {
            var partial = Path.Combine(temp.Combine("target"), remote + BackupNaming.PartialSuffix);
            if (first && !remote.EndsWith(Checksum.SidecarExtension))
            {
                // Send half of the file, then "lose the connection".
                first = false;
                Directory.CreateDirectory(temp.Combine("target"));
                var bytes = await File.ReadAllBytesAsync(local, ct);
                await File.WriteAllBytesAsync(partial, bytes[..(bytes.Length / 2)], ct);
                throw new IOException("Connection reset by peer");
            }

            if (File.Exists(partial))
            {
                resumedFrom = new FileInfo(partial).Length;
            }

            await inner.UploadAsync(local, remote, null, ct);
        });

        var setup = new Setup(temp, factory);
        var run = await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.True(resumedFrom > 0, "The second attempt should continue from the partial file.");
        Assert.Empty(Directory.GetFiles(temp.Combine("target"), "*" + BackupNaming.PartialSuffix));

        await RestoreService.RestoreFromFileAsync(temp.Combine("target", run.FileName!), new RestoreRequest(temp.Combine("restored"), "pw"), null, CancellationToken.None);
        Assert.Equal(File.ReadAllBytes(temp.Combine("source", "data.bin")), File.ReadAllBytes(temp.Combine("restored", "source", "data.bin")));
    }

    [Fact]
    public async Task Disk_full_on_destination_fails_loudly_and_cleans_up()
    {
        using var temp = new TempDirectory();
        var factory = new FaultyDestinationFactory((_, _, _, _) => throw new IOException("There is not enough space on the disk."));
        var setup = new Setup(temp, factory);

        var run = await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("not enough space", run.Message);
        Assert.Equal(3, factory.Uploads); // Retried according to the policy.
        Assert.Empty(Directory.GetDirectories(temp.Combine("staging")));
        Assert.Equal(RunStatus.Failed, setup.Runs.GetLast(setup.Job.Id)!.Status);
    }

    [Fact]
    public async Task Cancellation_mid_upload_marks_run_cancelled_and_cleans_staging()
    {
        using var temp = new TempDirectory();
        using var cts = new CancellationTokenSource();
        var factory = new FaultyDestinationFactory(async (_, _, _, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        });
        var setup = new Setup(temp, factory);

        var run = await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, cts.Token);

        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Empty(Directory.GetDirectories(temp.Combine("staging")));
    }

    [Fact]
    public async Task Service_killed_mid_run_is_recovered_on_next_start()
    {
        using var temp = new TempDirectory();
        var setup = new Setup(temp, new DestinationFactory());
        setup.Jobs.Save(setup.Job);

        // State left behind by a process killed during archiving: an open run and a half-written staging folder.
        var crashed = new BackupRun { JobId = setup.Job.Id, JobName = setup.Job.Name };
        setup.Runs.Insert(crashed);
        var leftover = Directory.CreateDirectory(temp.Combine("staging", crashed.Id.ToString("N")));
        File.WriteAllText(Path.Combine(leftover.FullName, "faults_20260101_000000.zip"), "half an archive");

        var scheduler = new BackupScheduler(setup.Jobs, setup.Runs, setup.Settings, setup.Runner, Array.Empty<INotifier>(), NullLogger<BackupScheduler>.Instance);
        scheduler.Recover(setup.Settings.Get());

        Assert.Equal(RunStatus.Interrupted, setup.Runs.GetRecent(setup.Job.Id).Single().Status);
        Assert.False(leftover.Exists && Directory.Exists(leftover.FullName));

        // The next run works normally.
        var run = await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, run.Status);
    }

    [Fact]
    public async Task Corrupted_archive_without_checksum_file_still_fails_loudly()
    {
        using var temp = new TempDirectory();
        var setup = new Setup(temp, new DestinationFactory());
        setup.Job.Processing.Encrypt = false;
        var run = await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, CancellationToken.None);
        var archive = temp.Combine("target", run.FileName!);

        File.Delete(archive + Checksum.SidecarExtension);
        await using (var stream = new FileStream(archive, FileMode.Open))
        {
            stream.SetLength(stream.Length / 2);
        }

        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            RestoreService.RestoreFromFileAsync(archive, new RestoreRequest(temp.Combine("restored")), null, CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_encrypted_archive_is_rejected_before_writing_output()
    {
        using var temp = new TempDirectory();
        var setup = new Setup(temp, new DestinationFactory());
        var run = await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, CancellationToken.None);
        var archive = temp.Combine("target", run.FileName!);

        File.Delete(archive + Checksum.SidecarExtension);
        await using (var stream = new FileStream(archive, FileMode.Open))
        {
            stream.SetLength(stream.Length - 100);
        }

        await Assert.ThrowsAnyAsync<Exception>(() =>
            RestoreService.RestoreFromFileAsync(archive, new RestoreRequest(temp.Combine("restored"), "pw"), null, CancellationToken.None));
        Assert.False(Directory.Exists(temp.Combine("restored")) && Directory.EnumerateFileSystemEntries(temp.Combine("restored")).Any());
    }

    [Fact]
    public async Task File_that_shrinks_after_enumeration_is_archived_consistently()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("changing.log", new string('x', 100_000));
        var entries = new List<ArchiveEntry> { new(path, "changing.log") };

        // Simulate an application truncating the file between enumeration and archiving.
        File.WriteAllText(path, "short");
        var zip = temp.Combine("out.zip");
        var result = await ArchiveBuilder.CreateAsync(entries, zip, ArchiveCompression.Optimal, null, CancellationToken.None);
        await ArchiveBuilder.VerifyAsync(zip, result.EntryCount, CancellationToken.None);

        await RestoreService.RestoreFromFileAsync(zip, new RestoreRequest(temp.Combine("restored")), null, CancellationToken.None);
        Assert.Equal("short", File.ReadAllText(temp.Combine("restored", "changing.log")));
    }
}
