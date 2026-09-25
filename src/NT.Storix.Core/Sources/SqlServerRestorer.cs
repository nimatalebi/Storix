using Microsoft.Data.SqlClient;

namespace NT.Storix.Core.Sources;

/// <summary>Restores a SQL Server <c>.bak</c> file into a database (optionally under a new name).</summary>
public static class SqlServerRestorer
{
    /// <param name="backupPath">Path of the .bak file as seen by the SQL Server engine.</param>
    /// <param name="databaseName">Target database name (created or replaced).</param>
    /// <param name="dataDirectory">Folder for data/log files; empty = instance default data folder.</param>
    /// <param name="replace">Overwrite an existing database with the same name.</param>
    public static async Task RestoreAsync(
        string connectionString,
        string backupPath,
        string databaseName,
        string? dataDirectory,
        bool replace,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds = 0,
        bool recover = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        dataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? await ScalarAsync(connection, "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000))", cancellationToken)
            : dataDirectory;
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new InvalidOperationException("Could not determine the SQL Server data folder. Specify it explicitly.");
        }

        // Read the logical file names to relocate them (WITH MOVE).
        var files = new List<(string Logical, string Type)>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "RESTORE FILELISTONLY FROM DISK = @path";
            list.CommandTimeout = commandTimeoutSeconds;
            list.Parameters.AddWithValue("@path", backupPath);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                files.Add((reader.GetString(reader.GetOrdinal("LogicalName")), reader.GetString(reader.GetOrdinal("Type"))));
            }
        }

        var separator = dataDirectory.Contains('/') && !dataDirectory.Contains('\\') ? "/" : "\\";
        var directory = dataDirectory.TrimEnd('/', '\\');

        await using var restore = connection.CreateCommand();
        restore.CommandTimeout = commandTimeoutSeconds;
        restore.Parameters.AddWithValue("@path", backupPath);

        var moves = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var extension = files[i].Type == "L" ? "ldf" : i == 0 ? "mdf" : "ndf";
            restore.Parameters.AddWithValue($"@logical{i}", files[i].Logical);
            restore.Parameters.AddWithValue($"@physical{i}", $"{directory}{separator}{databaseName}_{i}.{extension}");
            moves.Add($"MOVE @logical{i} TO @physical{i}");
        }

        if (replace)
        {
            // Close other connections so REPLACE can succeed.
            await using var single = connection.CreateCommand();
            single.CommandText = $"IF DB_ID(@name) IS NOT NULL ALTER DATABASE {Quote(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE";
            single.Parameters.AddWithValue("@name", databaseName);
            await single.ExecuteNonQueryAsync(cancellationToken);
        }

        restore.CommandText = $"RESTORE DATABASE {Quote(databaseName)} FROM DISK = @path WITH {string.Join(", ", moves)}, CHECKSUM{(replace ? ", REPLACE" : string.Empty)}, {(recover ? "RECOVERY" : "NORECOVERY")}";
        await restore.ExecuteNonQueryAsync(cancellationToken);

        if (replace && recover)
        {
            await using var multi = connection.CreateCommand();
            multi.CommandText = $"ALTER DATABASE {Quote(databaseName)} SET MULTI_USER";
            await multi.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Restores a chain (full, optional differential, logs) produced by <see cref="SqlRestoreChain.Plan"/>.
    /// </summary>
    /// <param name="files">Backup files in restore order with their type (paths as seen by SQL Server).</param>
    /// <param name="stopAtUtc">Point in time for the last log backup (optional).</param>
    public static async Task RestoreChainAsync(
        string connectionString,
        IReadOnlyList<(string Path, SqlBackupType Type)> files,
        string databaseName,
        string? dataDirectory,
        bool replace,
        DateTimeOffset? stopAtUtc,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds = 0)
    {
        if (files.Count == 0 || files[0].Type != SqlBackupType.Full)
        {
            throw new ArgumentException("A restore chain must start with a full backup.", nameof(files));
        }

        // Full backup WITH NORECOVERY (reusing the WITH MOVE logic).
        await RestoreAsync(connectionString, files[0].Path, databaseName, dataDirectory, replace, cancellationToken, commandTimeoutSeconds, recover: false);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        string? stopAtLocal = null;
        if (stopAtUtc is not null)
        {
            // STOPAT uses the server's local time.
            await using var offset = connection.CreateCommand();
            offset.CommandText = "SELECT DATEDIFF(MINUTE, SYSUTCDATETIME(), SYSDATETIME())";
            var minutes = (int)(await offset.ExecuteScalarAsync(cancellationToken))!;
            stopAtLocal = stopAtUtc.Value.UtcDateTime.AddMinutes(minutes).ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        }

        for (var i = 1; i < files.Count; i++)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = commandTimeoutSeconds;
            command.Parameters.AddWithValue("@path", files[i].Path);
            var isLast = i == files.Count - 1;
            var stopAt = files[i].Type == SqlBackupType.Log && isLast && stopAtLocal is not null ? ", STOPAT = @stopAt" : string.Empty;
            if (stopAt.Length > 0)
            {
                command.Parameters.AddWithValue("@stopAt", stopAtLocal!);
            }

            command.CommandText = files[i].Type == SqlBackupType.Log
                ? $"RESTORE LOG {Quote(databaseName)} FROM DISK = @path WITH CHECKSUM, NORECOVERY{stopAt}"
                : $"RESTORE DATABASE {Quote(databaseName)} FROM DISK = @path WITH CHECKSUM, NORECOVERY";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var recover = connection.CreateCommand())
        {
            recover.CommandTimeout = commandTimeoutSeconds;
            recover.CommandText = $"RESTORE DATABASE {Quote(databaseName)} WITH RECOVERY";
            await recover.ExecuteNonQueryAsync(cancellationToken);
        }

        if (replace)
        {
            await using var multi = connection.CreateCommand();
            multi.CommandText = $"ALTER DATABASE {Quote(databaseName)} SET MULTI_USER";
            await multi.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<string?> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    internal static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
}
