using System.Text.Json;
using Microsoft.Data.SqlClient;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <summary>
/// SQL Server backups with <c>CHECKSUM</c>: full (COPY_ONLY by default, so the server's own backup chain stays
/// intact), differential or transaction log. The LSN metadata of every backup is stored next to the .bak/.trn
/// file in the archive and returned for restore-chain tracking.
/// </summary>
public sealed class SqlServerSource(SqlServerSourceOptions options) : IBackupSource
{
    public async Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("SQL Server connection string is not configured.");
        }

        var databases = options.Databases.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (databases.Count == 0)
        {
            throw new InvalidOperationException("No SQL Server database selected.");
        }

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var directory = options.BackupDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = await GetDefaultBackupDirectoryAsync(connection, cancellationToken) ?? context.StagingDirectory;
        }

        var server = await ScalarAsync(connection, "SELECT CAST(@@SERVERNAME AS nvarchar(256))", cancellationToken) ?? "unknown";
        var entries = new List<ArchiveEntry>();
        var metadata = new List<SqlBackupInfo>();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var type = options.BackupType;
        var extension = type == SqlBackupType.Log ? "trn" : "bak";

        foreach (var database in databases)
        {
            var slug = Slug.From(database, "db");
            var path = Path.Combine(directory, $"{slug}_{stamp}_{Guid.NewGuid():N}.{extension}");

            context.Log.Info($"{type} backup of SQL Server database '{database}' to '{path}'.");
            var withOptions = new List<string> { "INIT", "FORMAT", "CHECKSUM" };
            if (type == SqlBackupType.Full && options.CopyOnly)
            {
                withOptions.Add("COPY_ONLY");
            }

            if (type == SqlBackupType.Differential)
            {
                withOptions.Add("DIFFERENTIAL");
            }

            if (options.NativeCompression)
            {
                withOptions.Add("COMPRESSION");
            }

            var statement = type == SqlBackupType.Log ? "BACKUP LOG" : "BACKUP DATABASE";
            await ExecuteAsync(connection, $"{statement} {Quote(database)} TO DISK = @path WITH {string.Join(", ", withOptions)}, NAME = @name", path, cancellationToken, database);

            if (options.VerifyBackup)
            {
                context.Log.Info($"Verifying backup of '{database}' (RESTORE VERIFYONLY).");
                await ExecuteAsync(connection, "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM", path, cancellationToken);
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"SQL Server wrote the backup to '{path}' but Storix cannot read it. When SQL Server runs on another machine, set 'Backup directory' to a shared (UNC) path.",
                    path);
            }

            var entryName = type switch
            {
                SqlBackupType.Differential => $"sqlserver/{slug}.diff.bak",
                SqlBackupType.Log => $"sqlserver/{slug}.trn",
                _ => $"sqlserver/{slug}.bak",
            };
            entries.Add(new ArchiveEntry(path, entryName) { DeleteAfterRun = true });

            var info = await ReadBackupInfoAsync(connection, server, database, path, cancellationToken);
            if (info is not null)
            {
                info.EntryName = entryName;
                metadata.Add(info);

                // Keep the metadata inside the archive too, so a chain can be rebuilt without the Storix database.
                var json = Path.Combine(context.StagingDirectory, $"{slug}.{type.ToString().ToLowerInvariant()}.storix.json");
                await File.WriteAllTextAsync(json, JsonSerializer.Serialize(info, StorixJson.Indented), cancellationToken);
                entries.Add(new ArchiveEntry(json, $"sqlserver/{slug}.storix.json"));
                context.Log.Info($"LSN range {info.FirstLsn} - {info.LastLsn}.");
            }
        }

        return new SourceSnapshot(entries) { SqlBackups = metadata };
    }

    private static async Task<SqlBackupInfo?> ReadBackupInfoAsync(SqlConnection connection, string server, string database, string path, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) bs.type, bs.first_lsn, bs.last_lsn, bs.checkpoint_lsn, bs.database_backup_lsn, bs.differential_base_lsn,
                   bs.is_copy_only,
                   DATEADD(MINUTE, -15 * ISNULL(bs.time_zone, 0), bs.backup_start_date),
                   DATEADD(MINUTE, -15 * ISNULL(bs.time_zone, 0), bs.backup_finish_date)
            FROM msdb.dbo.backupset bs
            JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id = bs.media_set_id
            WHERE mf.physical_device_name = @path AND bs.database_name = @db
            ORDER BY bs.backup_set_id DESC
            """;
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@db", database);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new SqlBackupInfo
            {
                Server = server,
                Database = database,
                Type = reader.GetString(0) switch { "I" => SqlBackupType.Differential, "L" => SqlBackupType.Log, _ => SqlBackupType.Full },
                FirstLsn = reader.GetDecimal(1),
                LastLsn = reader.GetDecimal(2),
                CheckpointLsn = reader.GetDecimal(3),
                DatabaseBackupLsn = reader.GetDecimal(4),
                DifferentialBaseLsn = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                IsCopyOnly = reader.GetBoolean(6),
                BackupStart = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)),
                BackupFinish = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)),
            };
        }
        catch (SqlException)
        {
            // No access to msdb: the backup is still valid, only chain tracking is unavailable.
            return null;
        }
    }

    private async Task ExecuteAsync(SqlConnection connection, string sql, string path, CancellationToken cancellationToken, string? name = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = Math.Max(0, options.CommandTimeoutSeconds);
        command.Parameters.AddWithValue("@path", path);
        if (name is not null)
        {
            command.Parameters.AddWithValue("@name", $"Storix backup of {name}");
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<string?> GetDefaultBackupDirectoryAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var value = await ScalarAsync(connection, "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))", cancellationToken);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
}
