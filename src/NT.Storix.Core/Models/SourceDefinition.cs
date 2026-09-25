namespace NT.Storix.Core.Models;

public enum SourceKind
{
    Files,
    SqlServer,
    MongoDb,
}

public sealed class SourceDefinition
{
    public SourceKind Kind { get; set; } = SourceKind.Files;

    public FileSourceOptions Files { get; set; } = new();

    public SqlServerSourceOptions SqlServer { get; set; } = new();

    public MongoDbSourceOptions MongoDb { get; set; } = new();
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
}

public sealed class SqlServerSourceOptions
{
    [Secret]
    public string? ConnectionString { get; set; } = "Server=localhost;Integrated Security=true;TrustServerCertificate=true";

    public List<string> Databases { get; set; } = [];

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
