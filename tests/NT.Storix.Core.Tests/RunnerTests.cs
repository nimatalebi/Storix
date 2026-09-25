using System.IO.Compression;
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

public class RunnerTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    /// <summary>Destination that fails the first N uploads to exercise retry.</summary>
    private sealed class FlakyDestinationFactory(int failures) : IDestinationFactory
    {
        private int _remaining = failures;

        public IBackupDestination Create(DestinationDefinition definition) => new Flaky(new LocalFolderDestination(definition.LocalFolder), this);

        private sealed class Flaky(LocalFolderDestination inner, FlakyDestinationFactory owner) : IBackupDestination
        {
            public Task TestAsync(CancellationToken cancellationToken) => inner.TestAsync(cancellationToken);

            public Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

            public Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken) =>
                Interlocked.Decrement(ref owner._remaining) >= 0
                    ? throw new IOException("Simulated network failure")
                    : inner.UploadAsync(localPath, remoteName, progress, cancellationToken);

            public Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken) =>
                inner.DownloadAsync(remoteName, localPath, progress, cancellationToken);

            public Task DeleteAsync(string remoteName, CancellationToken cancellationToken) => inner.DeleteAsync(remoteName, cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Full_pipeline_with_encryption_retry_and_retention()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("source/report.txt", "quarterly numbers");
        var target = temp.Combine("target");

        var database = new StorixDatabase(temp.Combine("storix.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        var runs = new RunRepository(database);

        var job = new BackupJob
        {
            Name = "Docs",
            Source = { Kind = SourceKind.Files, Files = { Paths = [temp.Combine("source")] } },
            Processing = { Encrypt = true, EncryptionPassword = "pw" },
            Destinations = [new DestinationDefinition { Name = "Local", Kind = DestinationKind.LocalFolder, LocalFolder = { Path = target } }],
            Retention = { KeepLast = 2 },
            Retry = { MaxAttempts = 3, InitialDelaySeconds = 0 },
        };

        // Old backups and a stale partial upload that must be cleaned up.
        Directory.CreateDirectory(target);
        foreach (var days in new[] { 3, 2, 1 })
        {
            File.WriteAllText(Path.Combine(target, BackupNaming.CreateFileName("docs", DateTimeOffset.UtcNow.AddDays(-days), encrypted: true)), "old");
        }

        File.WriteAllText(Path.Combine(target, "docs_20200101_000000.zip.aes" + BackupNaming.PartialSuffix), "partial");

        var runner = new BackupJobRunner(runs, settings, new SourceFactory(), new FlakyDestinationFactory(failures: 1), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
        var run = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Contains("attempt 1/3", run.Log);

        var archives = Directory.GetFiles(target, "*.aes").Select(Path.GetFileName).ToList();
        Assert.Equal(2, archives.Count); // KeepLast = 2
        Assert.Contains(run.FileName, archives);
        Assert.True(File.Exists(Path.Combine(target, run.FileName + Checksum.SidecarExtension)));
        Assert.Empty(Directory.GetFiles(target, "*" + BackupNaming.PartialSuffix));
        Assert.Empty(Directory.GetDirectories(temp.Combine("staging")));

        // The uploaded archive decrypts to a valid ZIP with the original content.
        var restored = temp.Combine("restored.zip");
        await AesFileEncryptor.DecryptAsync(Path.Combine(target, run.FileName!), restored, "pw", CancellationToken.None);
        using var zip = ZipFile.OpenRead(restored);
        using var reader = new StreamReader(zip.GetEntry("source/report.txt")!.Open());
        Assert.Equal("quarterly numbers", await reader.ReadToEndAsync());

        Assert.Equal(await Checksum.Sha256Async(Path.Combine(target, run.FileName!), CancellationToken.None), run.Sha256);
        Assert.Equal(RunStatus.Succeeded, runs.GetLast(job.Id)!.Status);
    }

    [Fact]
    public async Task Invalid_job_fails_and_is_recorded()
    {
        using var temp = new TempDirectory();
        var database = new StorixDatabase(temp.Combine("storix.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        var runs = new RunRepository(database);
        var runner = new BackupJobRunner(runs, settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);

        var run = await runner.RunAsync(new BackupJob { Name = "Broken" }, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("destination", run.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RunStatus.Failed, runs.GetLast(run.JobId)!.Status);
    }

    [Fact]
    public async Task Retry_gives_up_after_max_attempts()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => RetryExecutor.ExecuteAsync(
            new RetryPolicy { MaxAttempts = 3, InitialDelaySeconds = 1 },
            "op",
            (_, _) =>
            {
                attempts++;
                throw new IOException("boom");
            },
            null,
            CancellationToken.None,
            (_, _) => Task.CompletedTask));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Mongo_extra_arguments_are_split_respecting_quotes()
    {
        Assert.Equal(["--gzip", "--excludeCollection=logs", "--query=a b"], MongoDbSource.SplitArguments("--gzip  --excludeCollection=logs \"--query=a b\""));
    }
}
