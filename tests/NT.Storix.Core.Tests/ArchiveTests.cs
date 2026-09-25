using System.IO.Compression;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace NT.Storix.Core.Tests;

public class ArchiveTests
{
    [Fact]
    public async Task File_source_respects_excludes_and_archive_verifies()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("data/a.txt", "a");
        temp.WriteFile("data/sub/b.txt", "b");
        temp.WriteFile("data/skip.tmp", "tmp");
        temp.WriteFile("data/node_modules/x.js", "x");
        var single = temp.WriteFile("single.cfg", "cfg");

        var source = new FileSource(new FileSourceOptions
        {
            Paths = [temp.Combine("data"), single],
            ExcludePatterns = ["*.tmp", "node_modules"],
        });

        var snapshot = await source.PrepareAsync(new SourceContext(temp.Path, new RunLog(NullLogger.Instance, "test")), CancellationToken.None);
        var names = snapshot.Entries.Select(e => e.EntryName).Order().ToArray();
        Assert.Equal(["data/a.txt", "data/sub/b.txt", "single.cfg"], names);

        var zip = temp.Combine("out.zip");
        var result = await ArchiveBuilder.CreateAsync(snapshot.Entries, zip, ArchiveCompression.Optimal, null, CancellationToken.None);
        Assert.Equal(3, result.EntryCount);

        await ArchiveBuilder.VerifyAsync(zip, 3, CancellationToken.None);
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("data/sub/b.txt")!.Open());
        Assert.Equal("b", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Missing_optional_entry_is_skipped()
    {
        using var temp = new TempDirectory();
        var existing = temp.WriteFile("a.txt", "a");
        var warnings = new List<string>();

        var result = await ArchiveBuilder.CreateAsync(
            [new ArchiveEntry(existing, "a.txt", Optional: true), new ArchiveEntry(temp.Combine("missing.txt"), "missing.txt", Optional: true)],
            temp.Combine("out.zip"), ArchiveCompression.Fastest, warnings.Add, CancellationToken.None);

        Assert.Equal(1, result.EntryCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Single(warnings);
    }
}
