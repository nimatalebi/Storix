using System.IO.Compression;
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

public class RestoreTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    internal static async Task<(BackupJob Job, BackupRun Run)> BackupAsync(TempDirectory temp, bool encrypt)
    {
        var database = new StorixDatabase(temp.Combine("storix.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });

        var job = new BackupJob
        {
            Name = "Restore me",
            Source = { Files = { Paths = [temp.Combine("source")] } },
            Processing = { Encrypt = encrypt, EncryptionPassword = encrypt ? "pw" : null },
            Destinations = [new DestinationDefinition { Name = "Local", LocalFolder = { Path = temp.Combine("target") } }],
        };

        var runner = new BackupJobRunner(new RunRepository(database), settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
        var run = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        return (job, run);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backup_then_restore_from_destination_is_byte_identical(bool encrypt)
    {
        using var temp = new TempDirectory();
        var binary = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 3);
        Directory.CreateDirectory(temp.Combine("source", "nested"));
        await File.WriteAllBytesAsync(temp.Combine("source", "nested", "data.bin"), binary);
        temp.WriteFile("source/readme.txt", "hello");

        var (job, run) = await BackupAsync(temp, encrypt);
        var restore = new RestoreService(new DestinationFactory());

        var backups = await restore.ListBackupsAsync(job, job.Destinations[0], CancellationToken.None);
        Assert.Equal(run.FileName, Assert.Single(backups).Name);

        var result = await restore.RestoreFromDestinationAsync(job.Destinations[0], run.FileName!, new RestoreRequest(temp.Combine("restored"), "pw"), null, CancellationToken.None);

        Assert.True(result.ChecksumVerified);
        Assert.Equal(2, result.Files.Count);
        Assert.Equal(binary, await File.ReadAllBytesAsync(temp.Combine("restored", "source", "nested", "data.bin")));
        Assert.Equal("hello", await File.ReadAllTextAsync(temp.Combine("restored", "source", "readme.txt")));
    }

    [Fact]
    public async Task Encrypted_restore_requires_the_right_password()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("source/a.txt", "a");
        var (_, run) = await BackupAsync(temp, encrypt: true);
        var archive = temp.Combine("target", run.FileName!);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RestoreService.RestoreFromFileAsync(archive, new RestoreRequest(temp.Combine("r1")), null, CancellationToken.None));
        await Assert.ThrowsAsync<CryptographicException>(() => RestoreService.RestoreFromFileAsync(archive, new RestoreRequest(temp.Combine("r2"), "bad"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Corrupted_backup_fails_loudly()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("source/a.txt", new string('a', 50_000));
        var (_, run) = await BackupAsync(temp, encrypt: false);
        var archive = temp.Combine("target", run.FileName!);

        var bytes = await File.ReadAllBytesAsync(archive);
        bytes[bytes.Length / 2] ^= 0x5A;
        await File.WriteAllBytesAsync(archive, bytes);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => RestoreService.RestoreFromFileAsync(archive, new RestoreRequest(temp.Combine("r")), null, CancellationToken.None));
        Assert.Contains("Checksum mismatch", ex.Message);
    }

    [Fact]
    public async Task Existing_files_are_not_overwritten_unless_requested()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("source/a.txt", "new");
        var (_, run) = await BackupAsync(temp, encrypt: false);
        var archive = temp.Combine("target", run.FileName!);
        temp.WriteFile("restore/source/a.txt", "old");

        await Assert.ThrowsAsync<IOException>(() => RestoreService.RestoreFromFileAsync(archive, new RestoreRequest(temp.Combine("restore")), null, CancellationToken.None));
        Assert.Equal("old", File.ReadAllText(temp.Combine("restore", "source", "a.txt")));

        await RestoreService.RestoreFromFileAsync(archive, new RestoreRequest(temp.Combine("restore"), Overwrite: true), null, CancellationToken.None);
        Assert.Equal("new", File.ReadAllText(temp.Combine("restore", "source", "a.txt")));
    }

    [Fact]
    public async Task Zip_slip_entries_are_rejected()
    {
        using var temp = new TempDirectory();
        var zip = temp.Combine("evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../../escaped.txt").Open());
            writer.Write("pwned");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => RestoreService.RestoreFromFileAsync(zip, new RestoreRequest(temp.Combine("out")), null, CancellationToken.None));
        Assert.False(File.Exists(temp.Combine("escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(temp.Path)!, "escaped.txt")));
    }
}
