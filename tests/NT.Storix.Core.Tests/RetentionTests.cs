using NT.Storix.Core.Destinations;
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

public class GfsRetentionTests
{
    private static List<BackupFileInfo> Daily(DateTimeOffset newest, int days) =>
        Enumerable.Range(0, days)
            .Select(i => new BackupFileInfo($"job_{i}", newest.AddDays(-i), [$"job_{i}"]))
            .ToList();

    [Fact]
    public void Gfs_keeps_daily_weekly_monthly_and_yearly_representatives()
    {
        var now = new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero);
        var backups = Daily(now, 800); // More than two years of daily backups.
        var policy = new RetentionPolicy { KeepLast = 0, KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 12, KeepYearly = 3 };

        var delete = RetentionPlanner.SelectForDeletion(backups, policy, now, TimeZoneInfo.Utc);
        var kept = backups.Except(delete).OrderByDescending(b => b.CreatedAt).ToList();

        // 7 daily + up to 4 weekly + 12 monthly + 3 yearly, with overlaps.
        Assert.InRange(kept.Count, 12, 26);
        Assert.Equal(backups[0], kept[0]);
        Assert.All(backups.Take(7), b => Assert.Contains(b, kept));

        // One backup per month for the last 12 months: the newest of each month (month ends here).
        var monthly = kept.Where(b => b.CreatedAt.Day == DateTime.DaysInMonth(b.CreatedAt.Year, b.CreatedAt.Month)).Select(b => b.CreatedAt.ToString("yyyy-MM")).ToHashSet();
        Assert.True(monthly.Count >= 12);

        // Yearly: newest backup of 2026, 2025 and 2024.
        Assert.Contains(kept, b => b.CreatedAt.Year == 2024);
        Assert.DoesNotContain(kept, b => b.CreatedAt.Year < 2024);
    }

    [Fact]
    public void Gfs_protects_backups_that_keep_last_would_delete()
    {
        var now = new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero);
        var backups = Daily(now, 90);
        var policy = new RetentionPolicy { KeepLast = 3, KeepMonthly = 3 };

        var kept = backups.Except(RetentionPlanner.SelectForDeletion(backups, policy, now, TimeZoneInfo.Utc)).Select(b => b.CreatedAt.Date).OrderDescending().ToList();

        Assert.Equal([new DateTime(2026, 3, 31), new DateTime(2026, 3, 30), new DateTime(2026, 3, 29), new DateTime(2026, 2, 28), new DateTime(2026, 1, 31)], kept);
    }

    [Fact]
    public void Without_gfs_behaviour_is_unchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var backups = Daily(now, 10);
        Assert.Equal(7, RetentionPlanner.SelectForDeletion(backups, new RetentionPolicy { KeepLast = 3 }, now).Count);
        Assert.Empty(RetentionPlanner.SelectForDeletion(backups, new RetentionPolicy { KeepLast = 0 }, now));
    }
}
