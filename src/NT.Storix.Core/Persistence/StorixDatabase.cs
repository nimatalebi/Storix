using Microsoft.Data.Sqlite;

namespace NT.Storix.Core.Persistence;

/// <summary>
/// SQLite metadata store shared by the Windows service and the manager UI
/// (jobs, settings, run history and run requests).
/// </summary>
public sealed class StorixDatabase
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public StorixDatabase(string path)
    {
        Path = path;
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();

        Initialize();
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS jobs (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                enabled     INTEGER NOT NULL,
                definition  TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS runs (
                id           TEXT PRIMARY KEY,
                job_id       TEXT NOT NULL,
                job_name     TEXT NOT NULL,
                trigger      TEXT NOT NULL,
                status       TEXT NOT NULL,
                started_at   TEXT NOT NULL,
                finished_at  TEXT NULL,
                file_name    TEXT NULL,
                size_bytes   INTEGER NULL,
                sha256       TEXT NULL,
                message      TEXT NULL,
                log          TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_runs_job_started ON runs (job_id, started_at DESC);
            CREATE INDEX IF NOT EXISTS ix_runs_started ON runs (started_at DESC);

            CREATE TABLE IF NOT EXISTS sql_backups (
                id                     INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id                 TEXT NOT NULL,
                job_id                 TEXT NOT NULL,
                server                 TEXT NOT NULL,
                database_name          TEXT NOT NULL,
                type                   TEXT NOT NULL,
                first_lsn              TEXT NOT NULL,
                last_lsn               TEXT NOT NULL,
                checkpoint_lsn         TEXT NOT NULL,
                database_backup_lsn    TEXT NOT NULL,
                differential_base_lsn  TEXT NULL,
                is_copy_only           INTEGER NOT NULL,
                backup_start           TEXT NOT NULL,
                backup_finish          TEXT NOT NULL,
                entry_name             TEXT NOT NULL,
                archive_name           TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sql_backups_db ON sql_backups (server, database_name, backup_finish);

            CREATE TABLE IF NOT EXISTS audit_log (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                at         TEXT NOT NULL,
                user_name  TEXT NOT NULL,
                machine    TEXT NOT NULL,
                action     TEXT NOT NULL,
                target     TEXT NOT NULL,
                details    TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS paused_jobs (
                job_id     TEXT PRIMARY KEY,
                paused_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS drill_requests (
                job_id        TEXT PRIMARY KEY,
                requested_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS cancel_requests (
                job_id        TEXT PRIMARY KEY,
                requested_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS run_requests (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id        TEXT NOT NULL,
                requested_at  TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        using var version = connection.CreateCommand();
        version.CommandText = "SELECT COUNT(*) FROM schema_info";
        if ((long)version.ExecuteScalar()! == 0)
        {
            version.CommandText = $"INSERT INTO schema_info (version) VALUES ({SchemaVersion})";
            version.ExecuteNonQuery();
        }
    }
}
