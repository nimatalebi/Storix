using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Tests;

public class RetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static List<string> Files(int count) =>
        Enumerable.Range(0, count)
            .SelectMany(i =>
            {
                var name = BackupNaming.CreateFileName("job", Now.AddDays(-i), encrypted: i % 2 == 0);
                return new[] { name, name + Checksum.SidecarExtension };
            })
            .Append("job_garbage.zip")
            .Append("other-job_20260301_000000.zip")
            .ToList();

    [Fact]
    public void Parses_only_own_backups_with_sidecars()
    {
        var backups = BackupNaming.ParseBackups("job", Files(5));
        Assert.Equal(5, backups.Count);
        Assert.All(backups, b => Assert.Equal(2, b.Files.Count));
    }

    [Fact]
    public void Keep_last_deletes_oldest()
    {
        var backups = BackupNaming.ParseBackups("job", Files(10));
        var delete = RetentionPlanner.SelectForDeletion(backups, new RetentionPolicy { KeepLast = 3 }, Now);
        Assert.Equal(7, delete.Count);
        Assert.All(delete, b => Assert.True(b.CreatedAt <= Now.AddDays(-3)));
    }

    [Fact]
    public void Keep_days_deletes_old_but_never_the_newest()
    {
        var backups = BackupNaming.ParseBackups("job", Files(10));
        Assert.Equal(5, RetentionPlanner.SelectForDeletion(backups, new RetentionPolicy { KeepLast = 0, KeepDays = 4 }, Now).Count);

        var old = BackupNaming.ParseBackups("job", Files(1));
        Assert.Empty(RetentionPlanner.SelectForDeletion(old, new RetentionPolicy { KeepDays = 1 }, Now.AddYears(1)));
    }
}
