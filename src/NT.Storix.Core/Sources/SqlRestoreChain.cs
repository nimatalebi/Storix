namespace NT.Storix.Core.Sources;

/// <summary>Builds the sequence of backups needed to restore a SQL Server database to a point in time.</summary>
public static class SqlRestoreChain
{
    /// <summary>
    /// Plans full → (differential) → log backups. Without <paramref name="stopAtUtc"/> the latest possible state is
    /// restored. The returned list is in restore order.
    /// </summary>
    /// <exception cref="InvalidOperationException">No usable full backup or the log chain has a gap.</exception>
    public static IReadOnlyList<SqlBackupInfo> Plan(IEnumerable<SqlBackupInfo> backups, DateTimeOffset? stopAtUtc = null)
    {
        var all = backups.ToList();
        var limit = stopAtUtc ?? DateTimeOffset.MaxValue;

        var full = all.Where(b => b.Type == SqlBackupType.Full && b.BackupFinish <= limit)
                      .OrderByDescending(b => b.LastLsn)
                      .FirstOrDefault()
                   ?? throw new InvalidOperationException(stopAtUtc is null
                       ? "No full backup is available for this database."
                       : $"No full backup finished before {stopAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}.");

        var chain = new List<SqlBackupInfo> { full };

        // Differential backups can only be based on a non-copy-only full backup.
        var diff = full.IsCopyOnly
            ? null
            : all.Where(b => b.Type == SqlBackupType.Differential && b.DifferentialBaseLsn == full.CheckpointLsn && b.BackupFinish <= limit)
                 .OrderByDescending(b => b.LastLsn)
                 .FirstOrDefault();
        if (diff is not null)
        {
            chain.Add(diff);
        }

        var baseLsn = (diff ?? full).LastLsn;
        var logs = all.Where(b => b.Type == SqlBackupType.Log).OrderBy(b => b.FirstLsn).ToList();

        // The first log must contain the base LSN, every next log must start where the previous one ended.
        var current = logs.FirstOrDefault(l => l.FirstLsn <= baseLsn && l.LastLsn > baseLsn);
        if (current is null)
        {
            if (stopAtUtc is not null && logs.Any(l => l.LastLsn > baseLsn))
            {
                throw new InvalidOperationException("The transaction log chain does not connect to the selected full/differential backup.");
            }

            return chain;
        }

        while (current is not null)
        {
            chain.Add(current);
            if (current.BackupFinish >= limit)
            {
                break; // This log contains the requested point in time.
            }

            var previous = current;
            current = logs.FirstOrDefault(l => l.FirstLsn == previous.LastLsn);
            if (current is null && logs.Any(l => l.FirstLsn > previous.LastLsn) && stopAtUtc is not null && previous.BackupFinish < limit)
            {
                throw new InvalidOperationException($"The transaction log chain has a gap after LSN {previous.LastLsn}.");
            }
        }

        return chain;
    }
}
