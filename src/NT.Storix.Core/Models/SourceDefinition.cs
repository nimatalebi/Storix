using System.ComponentModel;

namespace NT.Storix.Core.Models;

public enum SourceKind
{
    Files,
    SqlServer,
    MongoDb,
    PostgreSql,
    MySql,
    Redis,
    Sqlite,
    /// <summary>Windows configuration: IIS, registry keys, scheduled tasks, certificates.</summary>
    WindowsSystem,
    DockerVolumes,
    HyperV,
}

public sealed class SourceDefinition
{
    public SourceKind Kind { get; set; } = SourceKind.Files;

    public FileSourceOptions Files { get; set; } = new();

    public SqlServerSourceOptions SqlServer { get; set; } = new();

    public MongoDbSourceOptions MongoDb { get; set; } = new();

    public PostgreSqlSourceOptions PostgreSql { get; set; } = new();

    public MySqlSourceOptions MySql { get; set; } = new();

    public RedisSourceOptions Redis { get; set; } = new();

    public SqliteSourceOptions Sqlite { get; set; } = new();

    public WindowsSystemSourceOptions WindowsSystem { get; set; } = new();

    public DockerVolumesSourceOptions DockerVolumes { get; set; } = new();

    public HyperVSourceOptions HyperV { get; set; } = new();

    /// <summary>Options object of the selected kind (for generic editors).</summary>
    public object ActiveOptions => Kind switch
    {
        SourceKind.Files => Files,
        SourceKind.SqlServer => SqlServer,
        SourceKind.MongoDb => MongoDb,
        SourceKind.PostgreSql => PostgreSql,
        SourceKind.MySql => MySql,
        SourceKind.Redis => Redis,
        SourceKind.Sqlite => Sqlite,
        SourceKind.WindowsSystem => WindowsSystem,
        SourceKind.DockerVolumes => DockerVolumes,
        SourceKind.HyperV => HyperV,
        _ => throw new NotSupportedException($"Source kind {Kind} is not supported."),
    };
}

public sealed class FileSourceOptions
{
    /// <summary>Files or folders to include.</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>Simple wildcard patterns (e.g. <c>*.tmp</c>, <c>node_modules</c>) matched against file and folder names.</summary>
    public List<string> ExcludePatterns { get; set; } = [];

    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>Skip files that are locked by another process instead of failing the whole job.</summary>
    public bool SkipLockedFiles { get; set; } = true;

    /// <summary>
    /// Windows: read files from a Volume Shadow Copy snapshot so open and locked files are backed up in a
    /// consistent state. Falls back to the live files (with a warning) if the snapshot cannot be created.
    /// </summary>
    public bool UseVss { get; set; }
}

public sealed class SqlServerSourceOptions
{
    [Secret]
    public string? ConnectionString { get; set; } = "Server=localhost;Integrated Security=true;TrustServerCertificate=true";

    public List<string> Databases { get; set; } = [];

    /// <summary>
    /// Full, differential or transaction-log backup. Differential and log backups need a full backup made by
    /// Storix with <see cref="CopyOnly"/> disabled (and the FULL recovery model for log backups).
    /// </summary>
    public Sources.SqlBackupType BackupType { get; set; } = Sources.SqlBackupType.Full;

    /// <summary>
    /// Full backups only: COPY_ONLY leaves the server's own backup chain untouched. Disable it when Storix
    /// manages differential/log backups of the database.
    /// </summary>
    public bool CopyOnly { get; set; } = true;

    /// <summary>
    /// Folder the SQL Server engine writes the <c>.bak</c> file to. It must be writable by the SQL Server
    /// service account and readable by Storix. Empty means the instance default backup directory.
    /// </summary>
    public string? BackupDirectory { get; set; }

    /// <summary>Runs <c>RESTORE VERIFYONLY ... WITH CHECKSUM</c> after the backup.</summary>
    public bool VerifyBackup { get; set; } = true;

    /// <summary>Uses SQL Server native backup compression (not available on Express edition).</summary>
    public bool NativeCompression { get; set; }

    /// <summary>Command timeout in seconds, 0 means no timeout.</summary>
    public int CommandTimeoutSeconds { get; set; }
}

public sealed class MongoDbSourceOptions
{
    /// <summary>Full path to <c>mongodump(.exe)</c>. Empty means it must be on PATH.</summary>
    public string? MongodumpPath { get; set; }

    [Secret]
    public string? ConnectionString { get; set; } = "mongodb://localhost:27017";

    /// <summary>Single database to dump. Empty dumps every database.</summary>
    public string? Database { get; set; }

    /// <summary>Include the oplog for a point-in-time consistent dump (replica sets, full dumps only).</summary>
    public bool UseOplog { get; set; }

    /// <summary>Extra raw arguments passed to mongodump.</summary>
    public string? ExtraArguments { get; set; }
}

public sealed class PostgreSqlSourceOptions
{
    [Category("Tool"), Description("Folder of the PostgreSQL client tools or full path to pg_dump(.exe). Empty = on PATH.")]
    public string? PgDumpPath { get; set; }

    [Category("Connection")]
    public string Host { get; set; } = "localhost";

    [Category("Connection")]
    public int Port { get; set; } = 5432;

    [Category("Connection")]
    public string UserName { get; set; } = "postgres";

    [Category("Connection"), PasswordPropertyText(true), Secret]
    public string? Password { get; set; }

    [Category("Backup"), Description("Comma-separated databases, dumped with pg_dump in custom format (-Fc). Empty = the whole cluster with pg_dumpall.")]
    public string? Databases { get; set; }
}

public sealed class MySqlSourceOptions
{
    [Category("Tool"), Description("Full path to mysqldump(.exe) or mariadb-dump(.exe). Empty = mysqldump on PATH.")]
    public string? MysqldumpPath { get; set; }

    [Category("Connection")]
    public string Host { get; set; } = "localhost";

    [Category("Connection")]
    public int Port { get; set; } = 3306;

    [Category("Connection")]
    public string UserName { get; set; } = "root";

    [Category("Connection"), PasswordPropertyText(true), Secret]
    public string? Password { get; set; }

    [Category("Backup"), Description("Comma-separated databases. Empty = all databases.")]
    public string? Databases { get; set; }

    [Category("Backup"), Description("--single-transaction: consistent InnoDB snapshot without locking tables.")]
    public bool SingleTransaction { get; set; } = true;
}

public sealed class RedisSourceOptions
{
    [Category("Tool"), Description("Full path to redis-cli(.exe). Empty = on PATH.")]
    public string? RedisCliPath { get; set; }

    [Category("Connection")]
    public string Host { get; set; } = "localhost";

    [Category("Connection")]
    public int Port { get; set; } = 6379;

    [Category("Connection"), Description("ACL user name (Redis 6+), optional.")]
    public string? UserName { get; set; }

    [Category("Connection"), PasswordPropertyText(true), Secret]
    public string? Password { get; set; }
}

public sealed class SqliteSourceOptions
{
    [Category("Backup"), Description("Comma-separated paths of SQLite database files. They are copied with the online backup API, so they stay consistent while in use.")]
    public string? DatabasePaths { get; set; }
}

public sealed class WindowsSystemSourceOptions
{
    [Category("IIS"), Description("Back up the IIS configuration (applicationHost.config and related files).")]
    public bool IisConfiguration { get; set; } = true;

    [Category("Registry"), Description(@"Comma-separated registry keys to export, e.g. HKLM\SOFTWARE\MyApp")]
    public string? RegistryKeys { get; set; }

    [Category("Scheduled tasks"), Description("Export all scheduled tasks as XML (except Microsoft's own).")]
    public bool ScheduledTasks { get; set; } = true;

    [Category("Certificates"), Description("Comma-separated LocalMachine certificate stores to export (public certificates), e.g. My, WebHosting")]
    public string? CertificateStores { get; set; } = "My";
}

public sealed class DockerVolumesSourceOptions
{
    [Category("Tool"), Description("Full path to docker(.exe). Empty = on PATH.")]
    public string? DockerPath { get; set; }

    [Category("Backup"), Description("Comma-separated volume names.")]
    public string? Volumes { get; set; }

    [Category("Backup"), Description("Small image used to read the volumes (needs tar).")]
    public string HelperImage { get; set; } = "alpine:3";

    [Category("Backup"), Description("Containers to stop during the backup for consistency (comma-separated), started again afterwards.")]
    public string? StopContainers { get; set; }
}

public sealed class HyperVSourceOptions
{
    [Category("Backup"), Description("Comma-separated virtual machine names. Running VMs are exported from a production checkpoint.")]
    public string? VirtualMachines { get; set; }
}
