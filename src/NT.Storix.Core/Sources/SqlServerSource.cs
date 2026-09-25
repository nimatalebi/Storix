using Microsoft.Data.SqlClient;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <summary>
/// Creates consistent full backups (<c>BACKUP DATABASE ... WITH COPY_ONLY, CHECKSUM</c>) of SQL Server databases.
/// COPY_ONLY keeps the existing backup chain of the server intact.
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

        var entries = new List<ArchiveEntry>();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

        foreach (var database in databases)
        {
            var fileName = $"{Slug.From(database, "db")}_{stamp}_{Guid.NewGuid():N}.bak";
            var path = Path.Combine(directory, fileName);

            context.Log.Info($"Backing up SQL Server database '{database}' to '{path}'.");
            var withClause = $"COPY_ONLY, INIT, FORMAT, CHECKSUM{(options.NativeCompression ? ", COMPRESSION" : string.Empty)}";
            await ExecuteAsync(connection, $"BACKUP DATABASE {Quote(database)} TO DISK = @path WITH {withClause}, NAME = @name", path, cancellationToken, database);

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

            entries.Add(new ArchiveEntry(path, $"sqlserver/{Slug.From(database, "db")}.bak") { DeleteAfterRun = true });
        }

        return new SourceSnapshot(entries);
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

    private static async Task<string?> GetDefaultBackupDirectoryAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))";
            var value = await command.ExecuteScalarAsync(cancellationToken) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
}
