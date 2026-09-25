using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Dedup;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

public class DedupTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    private static byte[] Random(int size) => RandomNumberGenerator.GetBytes(size);

    private static List<string> ChunkIds(byte[] data)
    {
        var keys = DedupKeys.Plain;
        return new FastCdc().Split(new MemoryStream(data)).Select(c => keys.ChunkId(c.Data.Span)).ToList();
    }

    [Fact]
    public void Chunks_respect_the_size_limits_and_cover_the_data()
    {
        var data = Random(9 * 1024 * 1024 + 17);
        var chunks = new FastCdc().Split(new MemoryStream(data)).Select(c => (c.Offset, c.Data.ToArray())).ToList();

        Assert.Equal(data.Length, chunks.Sum(c => c.Item2.Length));
        Assert.All(chunks.SkipLast(1), c => Assert.InRange(c.Item2.Length, FastCdc.DefaultMin, FastCdc.DefaultMax));
        Assert.Equal(data, chunks.SelectMany(c => c.Item2).ToArray());
    }

    [Fact]
    public void Inserting_bytes_only_changes_the_chunks_around_the_edit()
    {
        var original = Random(16 * 1024 * 1024);
        var edited = original[..(5 * 1024 * 1024)].Concat(Random(100)).Concat(original[(5 * 1024 * 1024)..]).ToArray();

        var before = ChunkIds(original);
        var after = ChunkIds(edited);

        var shared = before.Intersect(after).Count();
        Assert.True(shared >= before.Count - 3, $"Only {shared} of {before.Count} chunks survived a 100-byte insertion.");
    }

    [Fact]
    public void Chunk_ids_are_keyed_and_blobs_are_encrypted()
    {
        var data = Random(1000);
        var a = DedupKeys.Derive("pw", Random(16));
        var b = DedupKeys.Derive("pw", Random(16));

        Assert.NotEqual(a.ChunkId(data), b.ChunkId(data));
        Assert.Equal(DedupKeys.Plain.ChunkId(data), Convert.ToHexStringLower(SHA256.HashData(data))[..32]);
        var blob = a.Seal(data);
        Assert.Equal(data, a.Open(blob, data.Length));
        Assert.ThrowsAny<CryptographicException>(() => b.Open(blob, data.Length));
    }

    private sealed class Harness : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public Harness(bool encrypt, int keepLast = 10)
        {
            var database = new StorixDatabase(Combine("s.db"));
            Settings = new SettingsRepository(database, new PlainProtector());
            Settings.Save(new AppSettings { StagingDirectory = Combine("staging") });
            Runs = new RunRepository(database);
            Runner = new BackupJobRunner(Runs, Settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
            Job = new BackupJob
            {
                Name = "Dedup",
                Source = { Files = { Paths = [Combine("src")] } },
                Processing = { Deduplicate = true, Encrypt = encrypt, EncryptionPassword = encrypt ? "pw" : null },
                Retention = { KeepLast = keepLast },
                Destinations = [new DestinationDefinition { Name = "Local", LocalFolder = { Path = Combine("dst") } }],
            };
            Directory.CreateDirectory(Combine("src"));
        }

        public RunRepository Runs { get; }

        public SettingsRepository Settings { get; }

        public BackupJobRunner Runner { get; }

        public BackupJob Job { get; }

        public string Combine(params string[] parts) => _temp.Combine(parts);

        public async Task<BackupRun> RunAsync()
        {
            await Task.Delay(1100); // Backup names have a one-second resolution.
            var run = await Runner.RunAsync(Job, RunTrigger.Manual, CancellationToken.None);
            Assert.True(run.Status == RunStatus.Succeeded, run.Log);
            return run;
        }

        public string[] Packs() => Directory.GetFiles(Combine("dst"), "*.pack");

        public void Dispose() => _temp.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_new_data_is_uploaded_and_every_snapshot_restores(bool encrypt)
    {
        using var h = new Harness(encrypt);
        var big = Random(6 * 1024 * 1024);
        File.WriteAllBytes(h.Combine("src", "big.bin"), big);
        File.WriteAllBytes(h.Combine("src", "copy-of-big.bin"), big); // Deduplicated within the run.
        File.WriteAllText(h.Combine("src", "note.txt"), "v1");

        var first = await h.RunAsync();
        Assert.EndsWith(".snap", first.FileName);
        Assert.InRange(first.SizeBytes!.Value, 6L * 1024 * 1024, 7L * 1024 * 1024);

        var second = await h.RunAsync();
        Assert.Contains("0 new chunk(s)", second.Log);
        Assert.True(second.SizeBytes < 64 * 1024, $"Unchanged data uploaded {second.SizeBytes} bytes.");

        var edited = big.ToArray();
        edited[3 * 1024 * 1024] ^= 0xFF;
        File.WriteAllBytes(h.Combine("src", "big.bin"), edited);
        File.WriteAllText(h.Combine("src", "note.txt"), "v2");
        var third = await h.RunAsync();
        Assert.True(third.SizeBytes < 3L * 1024 * 1024, $"A one-byte change uploaded {third.SizeBytes} bytes.");

        var restore = new RestoreService(new DestinationFactory());
        var password = encrypt ? "pw" : null;
        var result = await restore.RestoreFromDestinationAsync(h.Job.Destinations[0], third.FileName!, new RestoreRequest(h.Combine("r3"), password), null, CancellationToken.None);
        Assert.True(result.ChecksumVerified);
        Assert.Equal(edited, File.ReadAllBytes(h.Combine("r3", "src", "big.bin")));
        Assert.Equal(big, File.ReadAllBytes(h.Combine("r3", "src", "copy-of-big.bin")));
        Assert.Equal("v2", File.ReadAllText(h.Combine("r3", "src", "note.txt")));

        // Older snapshot, straight from the folder (standalone restore).
        await RestoreService.RestoreFromFileAsync(h.Combine("dst", first.FileName!), new RestoreRequest(h.Combine("r1"), password), null, CancellationToken.None);
        Assert.Equal(big, File.ReadAllBytes(h.Combine("r1", "src", "big.bin")));
        Assert.Equal("v1", File.ReadAllText(h.Combine("r1", "src", "note.txt")));

        var backups = await restore.ListBackupsAsync(h.Job, h.Job.Destinations[0], CancellationToken.None);
        Assert.Equal(3, backups.Count);
        var index = await restore.GetIndexAsync(h.Job.Destinations[0], first.FileName!, password, CancellationToken.None);
        Assert.Equal(3, index.Entries.Count);
    }

    [Fact]
    public async Task Retention_removes_snapshots_and_packs_nobody_uses()
    {
        using var h = new Harness(encrypt: true, keepLast: 1);
        File.WriteAllBytes(h.Combine("src", "a.bin"), Random(2 * 1024 * 1024));
        await h.RunAsync();
        var firstPacks = h.Packs();

        File.WriteAllBytes(h.Combine("src", "a.bin"), Random(2 * 1024 * 1024)); // Completely new content.
        var second = await h.RunAsync();

        Assert.Single(Directory.GetFiles(h.Combine("dst"), "*.snap"));
        Assert.All(firstPacks, p => Assert.False(File.Exists(p), $"{Path.GetFileName(p)} should have been collected."));
        Assert.Contains("unused pack(s)", second.Log);
        await RestoreService.RestoreFromFileAsync(h.Combine("dst", second.FileName!), new RestoreRequest(h.Combine("r"), "pw"), null, CancellationToken.None);
    }

    [Fact]
    public async Task Wrong_password_and_corrupted_packs_are_detected()
    {
        using var h = new Harness(encrypt: true);
        File.WriteAllBytes(h.Combine("src", "a.bin"), Random(1024 * 1024));
        var run = await h.RunAsync();
        var snapshot = h.Combine("dst", run.FileName!);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => RestoreService.RestoreFromFileAsync(snapshot, new RestoreRequest(h.Combine("r1"), "wrong"), null, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RestoreService.RestoreFromFileAsync(snapshot, new RestoreRequest(h.Combine("r2")), null, CancellationToken.None));

        var pack = Assert.Single(h.Packs());
        var bytes = File.ReadAllBytes(pack);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(pack, bytes);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => RestoreService.RestoreFromFileAsync(snapshot, new RestoreRequest(h.Combine("r3"), "pw"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Restore_drill_verifies_a_deduplicated_backup()
    {
        using var h = new Harness(encrypt: true);
        File.WriteAllBytes(h.Combine("src", "a.bin"), Random(1024 * 1024));
        await h.RunAsync();
        var drills = new RestoreDrillRunner(h.Runs, h.Settings, new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<RestoreDrillRunner>.Instance);

        var drill = await drills.RunAsync(h.Job, CancellationToken.None);

        Assert.True(drill.Status == RunStatus.Succeeded, drill.Log);
    }

    [Fact]
    public void Dedup_settings_are_validated_and_snapshot_names_parse()
    {
        var job = new BackupJob
        {
            Source = { Files = { Paths = ["/x"], Incremental = true } },
            Processing = { Deduplicate = true, Encrypt = true, EncryptionMode = EncryptionMode.PublicKey, PublicKeyPem = "x" },
            Destinations = [new DestinationDefinition()],
        };

        var errors = BackupJobRunner.GetValidationErrors(job);

        Assert.Contains(errors, e => e.Contains("not with a public key"));
        Assert.Contains(errors, e => e.Contains("already incremental"));
        Assert.True(BackupNaming.TryParseArchive("job", "job_20260101_010203.snap", out _));
        Assert.False(BackupNaming.TryParseArchive("job", "job_pack_0123abcd.pack", out _));
        Assert.Equal("job", BackupNaming.PrefixOf("job_20260101_010203.snap"));
        Assert.True(DedupEngine.IsPack("job", "job_pack_0123abcd.pack"));
    }
}
