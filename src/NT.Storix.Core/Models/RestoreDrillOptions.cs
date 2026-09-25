namespace NT.Storix.Core.Models;

/// <summary>Periodic test restores that prove the backups can actually be restored.</summary>
public sealed class RestoreDrillOptions
{
    public bool Enabled { get; set; }

    /// <summary>Run a drill when the last one is older than this many days.</summary>
    public int EveryDays { get; set; } = 7;

    /// <summary>SQL Server jobs: restore every .bak into a temporary database and run DBCC CHECKDB.</summary>
    public bool CheckSqlDatabases { get; set; } = true;

    /// <summary>MongoDB jobs: validate the dump with <c>mongorestore --dryRun</c>.</summary>
    public bool CheckMongoArchive { get; set; } = true;
}
