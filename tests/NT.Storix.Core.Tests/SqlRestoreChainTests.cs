using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

public class SqlRestoreChainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private static SqlBackupInfo Full(decimal checkpoint, decimal last, int hour, bool copyOnly = false) =>
        new() { Type = SqlBackupType.Full, FirstLsn = checkpoint, CheckpointLsn = checkpoint, LastLsn = last, IsCopyOnly = copyOnly, BackupFinish = T0.AddHours(hour) };

    private static SqlBackupInfo Diff(decimal baseLsn, decimal first, decimal last, int hour) =>
        new() { Type = SqlBackupType.Differential, DifferentialBaseLsn = baseLsn, FirstLsn = first, LastLsn = last, BackupFinish = T0.AddHours(hour) };

    private static SqlBackupInfo Log(decimal first, decimal last, int hour) =>
        new() { Type = SqlBackupType.Log, FirstLsn = first, LastLsn = last, BackupFinish = T0.AddHours(hour) };

    [Fact]
    public void Latest_state_uses_full_latest_diff_and_following_logs()
    {
        var full = Full(100, 110, 0);
        var diff1 = Diff(100, 150, 160, 6);
        var diff2 = Diff(100, 250, 260, 12);
        var logs = new[] { Log(90, 120, 3), Log(120, 200, 7), Log(200, 255, 11), Log(255, 300, 14), Log(300, 350, 17) };

        var plan = SqlRestoreChain.Plan([full, diff1, diff2, .. logs]);

        Assert.Equal([full, diff2, logs[3], logs[4]], plan); // logs[2] ends (255) before the differential (260).
    }

    [Fact]
    public void Point_in_time_stops_at_the_log_that_contains_it()
    {
        var full = Full(100, 110, 0);
        var diff = Diff(100, 250, 260, 12);
        var logs = new[] { Log(90, 120, 3), Log(120, 200, 7), Log(200, 255, 11), Log(255, 300, 14), Log(300, 350, 17) };

        var plan = SqlRestoreChain.Plan([full, diff, .. logs], T0.AddHours(9));

        // The differential finished after 09:00, so logs are replayed from the full backup.
        Assert.Equal([full, logs[0], logs[1], logs[2]], plan);
    }

    [Fact]
    public void Copy_only_full_is_never_a_differential_base()
    {
        var full = Full(100, 110, 0, copyOnly: true);
        var diff = Diff(100, 150, 160, 6);

        Assert.Equal([full], SqlRestoreChain.Plan([full, diff]));
    }

    [Fact]
    public void Gaps_and_missing_full_are_reported()
    {
        Assert.Throws<InvalidOperationException>(() => SqlRestoreChain.Plan([Log(1, 2, 1)]));

        var full = Full(100, 110, 0);
        var broken = new[] { Log(90, 120, 1), Log(130, 140, 2) }; // 120 -> 130 missing.
        Assert.Throws<InvalidOperationException>(() => SqlRestoreChain.Plan([full, .. broken], T0.AddHours(2)));
    }
}
