using System.Globalization;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Sources;

/// <summary>Loads PostgreSQL and MySQL dumps produced by Storix back into a server.</summary>
public static class DumpRestorers
{
    /// <summary>
    /// Restores a PostgreSQL dump. Custom-format dumps (.dump) go into <paramref name="targetDatabase"/> (created first);
    /// cluster dumps (.sql from pg_dumpall) are replayed with psql.
    /// </summary>
    public static async Task RestorePostgreSqlAsync(PostgreSqlSourceOptions options, string dumpFile, string? targetDatabase, CancellationToken cancellationToken)
    {
        var source = new PostgreSqlSource(options);
        var environment = new Dictionary<string, string?> { ["PGPASSWORD"] = options.Password };
        var connection = new List<string> { "-h", options.Host, "-p", options.Port.ToString(CultureInfo.InvariantCulture), "-U", options.UserName, "--no-password" };

        if (dumpFile.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            await ExternalTool.RunAndCheckAsync(source.ToolPath("psql"), [.. connection, "-v", "ON_ERROR_STOP=1", "-f", dumpFile, "postgres"], environment, "psql", null, cancellationToken);
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetDatabase);
        await ExternalTool.RunAndCheckAsync(source.ToolPath("createdb"), [.. connection, targetDatabase], environment, "createdb", null, cancellationToken);
        await ExternalTool.RunAndCheckAsync(source.ToolPath("pg_restore"), [.. connection, "--no-owner", "--no-privileges", "--exit-on-error", "-d", targetDatabase, dumpFile], environment, "pg_restore", null, cancellationToken);
    }

    /// <summary>Replays a mysqldump file (it recreates the databases it contains).</summary>
    public static async Task RestoreMySqlAsync(MySqlSourceOptions options, string sqlFile, CancellationToken cancellationToken)
    {
        var dump = ExternalTool.Tool(options.MysqldumpPath, "mysqldump");
        var name = Path.GetFileNameWithoutExtension(dump);
        var client = name.StartsWith("mariadb", StringComparison.OrdinalIgnoreCase) ? "mariadb" : "mysql";
        var clientPath = dump.Contains(Path.DirectorySeparatorChar) || dump.Contains('/')
            ? Path.Combine(Path.GetDirectoryName(dump)!, client + (OperatingSystem.IsWindows() ? ".exe" : string.Empty))
            : client;

        var result = await ExternalTool.RunAsync(clientPath,
            ["-h", options.Host, "-P", options.Port.ToString(CultureInfo.InvariantCulture), "-u", options.UserName],
            new Dictionary<string, string?> { ["MYSQL_PWD"] = options.Password }, client, cancellationToken, standardInputFile: sqlFile);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{client} exited with code {result.ExitCode}: {result.Output}");
        }
    }
}
