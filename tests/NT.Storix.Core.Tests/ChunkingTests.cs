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

public class ChunkingTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    /// <summary>Local folder that fails the first upload of one chosen file.</summary>
    private sealed class FailOnceFactory(string failName) : IDestinationFactory
    {
        public List<string> Uploaded { get; } = [];

        public bool Failed { get; private set; }

        public IBackupDestination Create(DestinationDefinition definition) => new Wrapper(new LocalFolderDestination(definition.LocalFolder), this);

        private sealed class Wrapper(LocalFolderDestination inner, FailOnceFactory owner) : IBackupDestination
        {
            public Task TestAsync(CancellationToken cancellationToken) => inner.TestAsync(cancellationToken);

            public Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

            public Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
            {
                if (!owner.Failed && remoteName.EndsWith(owner.FailName(), StringComparison.Ordinal))
                {
                    owner.Failed = true;
                    throw new IOException("Connection lost");
                }

                owner.Uploaded.Add(remoteName);
                return inner.UploadAsync(localPath, remoteName, progress, cancellationToken);
            }

            public Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken) =>
                inner.DownloadAsync(remoteName, localPath, progress, cancellationToken);

            public Task DeleteAsync(string remoteName, CancellationToken cancellationToken) => inner.DeleteAsync(remoteName, cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private string FailName() => failName;
    }

    private static (BackupJobRunner Runner, BackupJob Job) Setup(TempDirectory temp, IDestinationFactory factory, int sizeBytes)
    {
        var database = new StorixDatabase(temp.Combine("s.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        Directory.CreateDirectory(temp.Combine("source"));
        File.WriteAllBytes(temp.Combine("source", "data.bin"), RandomNumberGenerator.GetBytes(sizeBytes));

        var job = new BackupJob
        {
            Name = "Split",
            Source = { Files = { Paths = [temp.Combine("source")] } },
            Processing = { Compression = ArchiveCompression.None, Encrypt = true, EncryptionPassword = "pw", SplitSizeMb = 1 },
            Destinations = [new DestinationDefinition { Name = "Local", LocalFolder = { Path = temp.Combine("dst") } }],
            Retry = { MaxAttempts = 3, InitialDelaySeconds = 0 },
            Retention = { KeepLast = 1 },
        };

        var runner = new BackupJobRunner(new RunRepository(database), settings, new SourceFactory(), factory, Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
        return (runner, job);
    }

    [Fact]
    public async Task Split_and_join_round_trip_and_detect_corruption()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("backup.zip");
        await File.WriteAllBytesAsync(file, RandomNumberGenerator.GetBytes(10_000));
        var hash = await Checksum.Sha256Async(file, CancellationToken.None);

        var manifest = await ChunkedArchive.SplitAsync(file, 4096, hash, CancellationToken.None);
        Assert.Equal([4096L, 4096L, 1808L], manifest.Chunks.Select(c => c.Size));
        Assert.Equal("backup.zip.part0001", manifest.Chunks[0].Name);

        var parsed = ChunkManifest.Parse(manifest.ToJson());
        await ChunkedArchive.JoinAsync(parsed, (c, _) => Task.FromResult(temp.Combine(c.Name)), temp.Combine("joined.zip"), deleteChunks: false, CancellationToken.None);
        Assert.Equal(await File.ReadAllBytesAsync(file), await File.ReadAllBytesAsync(temp.Combine("joined.zip")));

        var bytes = await File.ReadAllBytesAsync(temp.Combine("backup.zip.part0002"));
        bytes[10] ^= 1;
        await File.WriteAllBytesAsync(temp.Combine("backup.zip.part0002"), bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChunkedArchive.JoinAsync(parsed, (c, _) => Task.FromResult(temp.Combine(c.Name)), temp.Combine("joined2.zip"), deleteChunks: false, CancellationToken.None));
    }

    [Fact]
    public async Task Split_backup_uploads_volumes_and_restores_from_destination_and_folder()
    {
        using var temp = new TempDirectory();
        var (runner, job) = Setup(temp, new DestinationFactory(), (3 * 1024 * 1024) + 5000);

        var run = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var remote = Directory.GetFiles(temp.Combine("dst")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(4, remote.Count(n => n!.Contains(".part")));
        Assert.Contains(run.FileName + ChunkManifest.Extension, remote);
        Assert.Contains(run.FileName + Checksum.SidecarExtension, remote);
        Assert.DoesNotContain(run.FileName, remote);

        var original = File.ReadAllBytes(temp.Combine("source", "data.bin"));
        var restore = new RestoreService(new DestinationFactory());
        Assert.Equal(run.FileName, Assert.Single(await restore.ListBackupsAsync(job, job.Destinations[0], CancellationToken.None)).Name);

        await restore.RestoreFromDestinationAsync(job.Destinations[0], run.FileName!, new RestoreRequest(temp.Combine("r1"), "pw"), null, CancellationToken.None);
        Assert.Equal(original, File.ReadAllBytes(temp.Combine("r1", "source", "data.bin")));

        // Restore by pointing at any volume in a folder.
        await RestoreService.RestoreFromFileAsync(temp.Combine("dst", run.FileName + ".part0003"), new RestoreRequest(temp.Combine("r2"), "pw"), null, CancellationToken.None);
        Assert.Equal(original, File.ReadAllBytes(temp.Combine("r2", "source", "data.bin")));

        // Retention (keep last 1) removes every volume of the previous backup.
        await Task.Delay(1100);
        var second = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, second.Status);
        Assert.All(Directory.GetFiles(temp.Combine("dst")), f => Assert.StartsWith(second.FileName!, Path.GetFileName(f)));
    }

    [Fact]
    public async Task Failed_volume_upload_resumes_without_resending_earlier_volumes()
    {
        using var temp = new TempDirectory();
        var factory = new FailOnceFactory(".part0003");
        var (runner, job) = Setup(temp, factory, (3 * 1024 * 1024) + 5000);

        var run = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.True(factory.Failed);
        Assert.Equal(1, factory.Uploaded.Count(n => n.EndsWith(".part0001")));
        Assert.Equal(1, factory.Uploaded.Count(n => n.EndsWith(".part0002")));
        Assert.Contains("already present", run.Log);
    }

    [Fact]
    public void Incomplete_volume_sets_are_not_backups_and_are_reported_as_orphans()
    {
        var names = new[]
        {
            "job_20260101_000000.zip.aes.part0001", "job_20260101_000000.zip.aes.part0002", // no manifest
            "job_20260102_000000.zip.aes.part0001", "job_20260102_000000.zip.aes.manifest.json",
            "job_20260103_000000.zip", "other_20260101_000000.zip.part0001",
        };

        var backups = BackupNaming.ParseBackups("job", names).OrderBy(b => b.CreatedAt).ToList();
        Assert.Equal(["job_20260102_000000.zip.aes", "job_20260103_000000.zip"], backups.Select(b => b.Name));
        Assert.Equal(2, backups[0].Files.Count);

        Assert.Equal(
            ["job_20260101_000000.zip.aes.part0001", "job_20260101_000000.zip.aes.part0002"],
            BackupNaming.OrphanedFiles("job", names, keep: null).Order());
        Assert.Empty(BackupNaming.OrphanedFiles("job", names, keep: "job_20260101_000000.zip.aes"));
    }
}
