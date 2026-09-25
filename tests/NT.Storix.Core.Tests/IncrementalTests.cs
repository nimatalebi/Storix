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

public class IncrementalTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static ArchiveEntry Entry(string name) => new($"/src/{name}", name);

    private static IncrementalState State(string baseArchive, DateTimeOffset lastFull, params (string Name, long Size, long Modified)[] files) => new()
    {
        BaseArchive = baseArchive,
        LastFullAt = lastFull,
        Settings = "s",
        Files = files.ToDictionary(f => f.Name, f => new FileStateEntry { Size = f.Size, Modified = f.Modified }, StringComparer.Ordinal),
    };

    [Fact]
    public void Planner_selects_new_changed_and_deleted_files()
    {
        var previous = State("b1", Now.AddDays(-1), ("same.txt", 1, 1), ("changed.txt", 1, 1), ("gone.txt", 1, 1));
        var stats = new Dictionary<string, FileStateEntry>
        {
            ["same.txt"] = new() { Size = 1, Modified = 1 },
            ["changed.txt"] = new() { Size = 1, Modified = 2 },
            ["new.txt"] = new() { Size = 5, Modified = 1 },
        };

        var plan = IncrementalPlanner.Plan([Entry("same.txt"), Entry("changed.txt"), Entry("new.txt")], previous, "b1", "s", 7, Now, e => stats[e.EntryName]);

        Assert.False(plan.Full);
        Assert.Equal(["changed.txt", "new.txt"], plan.Entries.Select(e => e.EntryName));
        Assert.Equal(["gone.txt"], plan.Deleted);
        Assert.Equal(3, plan.State.Files.Count);
        Assert.Equal(previous.LastFullAt, plan.State.LastFullAt);
    }

    [Theory]
    [InlineData(null, "s", 1, "no previous")]
    [InlineData("other", "s", 1, "did not complete")]
    [InlineData("b1", "changed", 1, "settings changed")]
    [InlineData("b1", "s", 8, "day(s) old")]
    public void Planner_falls_back_to_a_full_backup(string? lastArchive, string settings, int daysSinceFull, string reason)
    {
        var previous = lastArchive is null ? null : State("b1", Now.AddDays(-daysSinceFull), ("a.txt", 1, 1));

        var plan = IncrementalPlanner.Plan([Entry("a.txt")], previous, lastArchive, settings, 7, Now, _ => new FileStateEntry { Size = 1, Modified = 1 });

        Assert.True(plan.Full);
        Assert.Contains(reason, plan.Reason);
        Assert.Single(plan.Entries);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_backed_up_again_and_not_reported_deleted()
    {
        var previous = State("b1", Now, ("locked.txt", 1, 1));

        var plan = IncrementalPlanner.Plan([Entry("locked.txt")], previous, "b1", "s", 7, Now, _ => null);

        Assert.Single(plan.Entries);
        Assert.Empty(plan.Deleted);
    }

    [Fact]
    public void State_round_trips_and_garbage_is_ignored()
    {
        var state = State("b1", Now, ("a.txt", 3, 4));

        var copy = IncrementalState.FromBytes(state.ToBytes())!;

        Assert.Equal("b1", copy.BaseArchive);
        Assert.Equal(3, copy.Files["a.txt"].Size);
        Assert.Null(IncrementalState.FromBytes([1, 2, 3]));
    }

    [Fact]
    public void Retention_never_breaks_a_kept_chain()
    {
        // full(-6) inc(-5) inc(-4) full(-3) inc(-2) inc(-1): keeping the last 2 must keep their full backup too.
        var names = new[] { false, true, true, false, true, true }
            .Select((inc, i) => BackupNaming.CreateFileName("job", Now.AddDays(i - 6), encrypted: true, incremental: inc))
            .ToList();
        var backups = BackupNaming.ParseBackups("job", names);

        var deleted = RetentionPlanner.SelectForDeletion(backups, new RetentionPolicy { KeepLast = 2 }, Now).Select(b => b.Name).ToList();

        Assert.Equal(names.Take(3).Order(), deleted.Order());
        Assert.True(BackupNaming.IsIncremental(names[1]));
        Assert.Equal("job", BackupNaming.PrefixOf(names[1]));
        Assert.Equal(names.Skip(3).ToList(), BackupNaming.ChainOf(backups, names[5]).Select(b => b.Name).ToList());
        Assert.Throws<InvalidDataException>(() => BackupNaming.ChainOf(BackupNaming.ParseBackups("job", names.Skip(1)), names[2]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incremental_chain_restores_the_latest_state(bool split)
    {
        using var temp = new TempDirectory();
        var database = new StorixDatabase(temp.Combine("storix.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        var runs = new RunRepository(database);
        var runner = new BackupJobRunner(runs, settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
        var job = new BackupJob
        {
            Name = "Docs",
            Source = { Files = { Paths = [temp.Combine("src")], Incremental = true } },
            Processing = { Encrypt = true, EncryptionPassword = "pw", SplitSizeMb = split ? 1 : 0, Compression = ArchiveCompression.None },
            Retention = { KeepLast = 10 },
            Destinations = [new DestinationDefinition { Name = "Local", LocalFolder = { Path = temp.Combine("dst") } }],
        };

        temp.WriteFile("src/keep.txt", "keep");
        temp.WriteFile("src/change.txt", "v1");
        temp.WriteFile("src/delete.txt", "bye");
        if (split)
        {
            File.WriteAllBytes(temp.Combine("src", "big.bin"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        }

        var full = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, full.Status);
        Assert.False(BackupNaming.IsIncremental(full.FileName!));
        Assert.Contains("Full backup (no previous file list)", full.Log);

        await Task.Delay(1100);
        temp.WriteFile("src/change.txt", "version two");
        File.Delete(temp.Combine("src", "delete.txt"));
        temp.WriteFile("src/new/added.txt", "added");
        var inc1 = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, inc1.Status);
        Assert.True(BackupNaming.IsIncremental(inc1.FileName!));
        Assert.Contains("2 changed and 1 deleted", inc1.Log);
        if (split)
        {
            Assert.True(inc1.SizeBytes < full.SizeBytes / 10, "The unchanged 2 MB file must not be in the incremental backup.");
        }

        await Task.Delay(1100);
        var inc2 = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);
        Assert.Contains("0 changed and 0 deleted", inc2.Log);

        // From the destination (downloads the whole chain)...
        var restore = new RestoreService(new DestinationFactory());
        await restore.RestoreFromDestinationAsync(job.Destinations[0], inc2.FileName!, new RestoreRequest(temp.Combine("r1"), "pw"), null, CancellationToken.None);
        AssertLatest(temp.Combine("r1"), split);

        // ...and from a folder with the files (standalone restore).
        var local = split ? temp.Combine("dst", inc2.FileName + ChunkManifest.Extension) : temp.Combine("dst", inc2.FileName!);
        if (split && !File.Exists(local))
        {
            local = temp.Combine("dst", inc2.FileName!);
        }

        await RestoreService.RestoreFromFileAsync(local, new RestoreRequest(temp.Combine("r2"), "pw"), null, CancellationToken.None);
        AssertLatest(temp.Combine("r2"), split);

        // Restoring the full backup alone gives the old state.
        await restore.RestoreFromDestinationAsync(job.Destinations[0], full.FileName!, new RestoreRequest(temp.Combine("r3"), "pw"), null, CancellationToken.None);
        Assert.Equal("v1", File.ReadAllText(temp.Combine("r3", "src", "change.txt")));
        Assert.True(File.Exists(temp.Combine("r3", "src", "delete.txt")));

        // The index of an incremental backup lists only its changes.
        var index = await restore.GetIndexAsync(job.Destinations[0], inc1.FileName!, "pw", CancellationToken.None);
        Assert.Equal(["src/change.txt", "src/new/added.txt"], index.Entries.Select(e => e.Path).Order());
    }

    private static void AssertLatest(string root, bool split)
    {
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "src", "keep.txt")));
        Assert.Equal("version two", File.ReadAllText(Path.Combine(root, "src", "change.txt")));
        Assert.Equal("added", File.ReadAllText(Path.Combine(root, "src", "new", "added.txt")));
        Assert.False(File.Exists(Path.Combine(root, "src", "delete.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, ".storix")));
        Assert.Equal(split, File.Exists(Path.Combine(root, "src", "big.bin")));
    }

    [Fact]
    public async Task Failed_destination_forces_the_next_backup_to_be_full()
    {
        using var temp = new TempDirectory();
        var database = new StorixDatabase(temp.Combine("storix.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        var runs = new RunRepository(database);
        var runner = new BackupJobRunner(runs, settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
        temp.WriteFile("src/a.txt", "a");
        temp.WriteFile("blocker", "a file, so no folder can be created below it");
        var job = new BackupJob
        {
            Name = "Partial",
            Source = { Files = { Paths = [temp.Combine("src")], Incremental = true } },
            Retry = { MaxAttempts = 1 },
            Destinations =
            [
                new DestinationDefinition { Name = "Good", LocalFolder = { Path = temp.Combine("good") } },
                new DestinationDefinition { Name = "Bad", LocalFolder = { Path = temp.Combine("blocker", "sub") } },
            ],
        };

        var first = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);
        Assert.Equal(RunStatus.PartiallySucceeded, first.Status);
        Assert.Null(runs.GetFileState(job.Id));

        job.Destinations.RemoveAt(1);
        await Task.Delay(1100);
        var second = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);
        Assert.False(BackupNaming.IsIncremental(second.FileName!));
        Assert.NotNull(runs.GetFileState(job.Id));
    }
}
