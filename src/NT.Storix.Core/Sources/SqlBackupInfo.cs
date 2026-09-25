namespace NT.Storix.Core.Sources;

public enum SqlBackupType
{
    Full,
    Differential,
    Log,
}

/// <summary>Metadata of one SQL Server backup, read from msdb after the backup (used to build restore chains).</summary>
public sealed class SqlBackupInfo
{
    public string Server { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public SqlBackupType Type { get; set; }

    public decimal FirstLsn { get; set; }

    public decimal LastLsn { get; set; }

    public decimal CheckpointLsn { get; set; }

    public decimal DatabaseBackupLsn { get; set; }

    public decimal? DifferentialBaseLsn { get; set; }

    public bool IsCopyOnly { get; set; }

    public DateTimeOffset BackupStart { get; set; }

    public DateTimeOffset BackupFinish { get; set; }

    /// <summary>Path of the backup file inside the Storix archive (e.g. <c>sqlserver/sales.trn</c>).</summary>
    public string EntryName { get; set; } = string.Empty;

    // Filled in when the backup has been stored.

    public Guid JobId { get; set; }

    public Guid RunId { get; set; }

    /// <summary>Name of the Storix archive holding this backup.</summary>
    public string? ArchiveName { get; set; }

    public override string ToString() => $"{Type} {Database} {BackupFinish.ToLocalTime():yyyy-MM-dd HH:mm:ss} (LSN {FirstLsn}-{LastLsn})";
}
