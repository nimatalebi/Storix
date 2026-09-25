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
using ZstdSharp;

namespace NT.Storix.Core.Tests;

public class ZstdTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    [Theory]
    [InlineData(ArchiveCompression.Zstd, false)]
    [InlineData(ArchiveCompression.ZstdSmallest, true)]
    public async Task Zstd_backup_restores_byte_identical(ArchiveCompression compression, bool encrypt)
    {
        using var temp = new TempDirectory();
        var binary = RandomNumberGenerator.GetBytes(1024 * 1024);
        Directory.CreateDirectory(temp.Combine("source"));
        await File.WriteAllBytesAsync(temp.Combine("source", "random.bin"), binary);
        temp.WriteFile("source/text.txt", string.Concat(Enumerable.Repeat("compressible line of text\n", 20_000)));

        var database = new StorixDatabase(temp.Combine("storix.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        var job = new BackupJob
        {
            Name = "zstd",
            Source = { Files = { Paths = [temp.Combine("source")] } },
            Processing = { Compression = compression, Encrypt = encrypt, EncryptionPassword = encrypt ? "pw" : null, VerifyArchive = true },
            Destinations = [new DestinationDefinition { Name = "Local", LocalFolder = { Path = temp.Combine("target") } }],
        };
        var runner = new BackupJobRunner(new RunRepository(database), settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);

        var run = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.EndsWith(encrypt ? ".zip.zst.aes" : ".zip.zst", run.FileName);
        Assert.True(run.SizeBytes < 1024 * 1024 + 100_000, $"Archive is {run.SizeBytes} bytes; the text file should compress well.");

        var restore = new RestoreService(new DestinationFactory());
        Assert.Equal(run.FileName, Assert.Single(await restore.ListBackupsAsync(job, job.Destinations[0], CancellationToken.None)).Name);
        var index = await restore.GetIndexAsync(job.Destinations[0], run.FileName!, "pw", CancellationToken.None);
        Assert.Equal(2, index.Entries.Count);

        await restore.RestoreFromDestinationAsync(job.Destinations[0], run.FileName!, new RestoreRequest(temp.Combine("restored"), "pw"), null, CancellationToken.None);
        Assert.Equal(binary, await File.ReadAllBytesAsync(temp.Combine("restored", "source", "random.bin")));
        Assert.Empty(Directory.GetFiles(temp.Combine("target"), "*.tmp"));
    }

    [Fact]
    public async Task Zstd_archive_is_a_standard_zstd_frame_around_a_zip()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("in/a.txt", "hello");
        var path = temp.Combine("a.zip.zst");

        await ArchiveBuilder.CreateAsync([new ArchiveEntry(temp.Combine("in", "a.txt"), "a.txt")], path, ArchiveCompression.Zstd, null, CancellationToken.None);

        Assert.True(ArchiveBuilder.IsZstdFile(path));
        await using var input = File.OpenRead(path);
        await using var zstd = new DecompressionStream(input);
        using var plain = new MemoryStream();
        await zstd.CopyToAsync(plain);
        plain.Position = 0;
        using var zip = new ZipArchive(plain, ZipArchiveMode.Read);
        using var reader = new StreamReader(Assert.Single(zip.Entries).Open());
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public void Zstd_names_are_recognized_and_plain_zip_is_not_zstd()
    {
        var name = BackupNaming.CreateFileName("job", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), encrypted: true, zstd: true);

        Assert.Equal("job_20260102_030405.zip.zst.aes", name);
        Assert.True(BackupNaming.TryParseArchive("job", name, out var created));
        Assert.Equal(2026, created.Year);
        Assert.True(BackupNaming.TryParseArchive("job", "job_20260102_030405.zip.zst", out _));
        Assert.False(BackupNaming.TryParseArchive("job", "job_20260102_030405.zst", out _));

        using var temp = new TempDirectory();
        temp.WriteFile("plain.zip", "PK\u0003\u0004");
        Assert.False(ArchiveBuilder.IsZstdFile(temp.Combine("plain.zip")));
    }
}
