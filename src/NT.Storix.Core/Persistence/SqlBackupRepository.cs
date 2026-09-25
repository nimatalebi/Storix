using System.Globalization;
using Microsoft.Data.Sqlite;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Persistence;

/// <summary>SQL Server backup chain metadata (LSNs), used for point-in-time restores.</summary>
public sealed class SqlBackupRepository(StorixDatabase database)
{
    public void Add(SqlBackupInfo info)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sql_backups (run_id, job_id, server, database_name, type, first_lsn, last_lsn, checkpoint_lsn,
                                     database_backup_lsn, differential_base_lsn, is_copy_only, backup_start, backup_finish,
                                     entry_name, archive_name)
            VALUES ($run, $job, $server, $db, $type, $first, $last, $checkpoint, $dbBackup, $diffBase, $copyOnly, $start, $finish, $entry, $archive)
            """;
        command.Parameters.AddWithValue("$run", info.RunId.ToString());
        command.Parameters.AddWithValue("$job", info.JobId.ToString());
        command.Parameters.AddWithValue("$server", info.Server);
        command.Parameters.AddWithValue("$db", info.Database);
        command.Parameters.AddWithValue("$type", info.Type.ToString());
        command.Parameters.AddWithValue("$first", Lsn(info.FirstLsn));
        command.Parameters.AddWithValue("$last", Lsn(info.LastLsn));
        command.Parameters.AddWithValue("$checkpoint", Lsn(info.CheckpointLsn));
        command.Parameters.AddWithValue("$dbBackup", Lsn(info.DatabaseBackupLsn));
        command.Parameters.AddWithValue("$diffBase", info.DifferentialBaseLsn is { } b ? Lsn(b) : DBNull.Value);
        command.Parameters.AddWithValue("$copyOnly", info.IsCopyOnly ? 1 : 0);
        command.Parameters.AddWithValue("$start", info.BackupStart.ToString("O"));
        command.Parameters.AddWithValue("$finish", info.BackupFinish.ToString("O"));
        command.Parameters.AddWithValue("$entry", info.EntryName);
        command.Parameters.AddWithValue("$archive", (object?)info.ArchiveName ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Distinct (server, database) pairs that have recorded backups.</summary>
    public IReadOnlyList<(string Server, string Database)> ListDatabases()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT server, database_name FROM sql_backups ORDER BY server, database_name";
        using var reader = command.ExecuteReader();
        var result = new List<(string, string)>();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    public IReadOnlyList<SqlBackupInfo> GetBackups(string server, string databaseName)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, job_id, server, database_name, type, first_lsn, last_lsn, checkpoint_lsn, database_backup_lsn,
                   differential_base_lsn, is_copy_only, backup_start, backup_finish, entry_name, archive_name
            FROM sql_backups WHERE server = $server AND database_name = $db ORDER BY backup_finish
            """;
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$db", databaseName);
        using var reader = command.ExecuteReader();
        var result = new List<SqlBackupInfo>();
        while (reader.Read())
        {
            result.Add(Read(reader));
        }

        return result;
    }

    /// <summary>Removes metadata of archives that no longer exist (after retention).</summary>
    public int DeleteByArchive(Guid jobId, IEnumerable<string> archiveNames)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var deleted = 0;
        foreach (var name in archiveNames)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM sql_backups WHERE job_id = $job AND archive_name = $archive";
            command.Parameters.AddWithValue("$job", jobId.ToString());
            command.Parameters.AddWithValue("$archive", name);
            deleted += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return deleted;
    }

    private static SqlBackupInfo Read(SqliteDataReader r) => new()
    {
        RunId = Guid.Parse(r.GetString(0)),
        JobId = Guid.Parse(r.GetString(1)),
        Server = r.GetString(2),
        Database = r.GetString(3),
        Type = Enum.Parse<SqlBackupType>(r.GetString(4)),
        FirstLsn = decimal.Parse(r.GetString(5), CultureInfo.InvariantCulture),
        LastLsn = decimal.Parse(r.GetString(6), CultureInfo.InvariantCulture),
        CheckpointLsn = decimal.Parse(r.GetString(7), CultureInfo.InvariantCulture),
        DatabaseBackupLsn = decimal.Parse(r.GetString(8), CultureInfo.InvariantCulture),
        DifferentialBaseLsn = r.IsDBNull(9) ? null : decimal.Parse(r.GetString(9), CultureInfo.InvariantCulture),
        IsCopyOnly = r.GetInt32(10) == 1,
        BackupStart = DateTimeOffset.Parse(r.GetString(11), CultureInfo.InvariantCulture),
        BackupFinish = DateTimeOffset.Parse(r.GetString(12), CultureInfo.InvariantCulture),
        EntryName = r.GetString(13),
        ArchiveName = r.IsDBNull(14) ? null : r.GetString(14),
    };

    // LSNs are numeric(25,0): stored as text to keep every digit.
    private static string Lsn(decimal value) => value.ToString("0", CultureInfo.InvariantCulture);
}
