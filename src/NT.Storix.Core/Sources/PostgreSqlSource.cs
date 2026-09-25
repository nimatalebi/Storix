using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <summary>PostgreSQL: pg_dump in custom format per database, or pg_dumpall for the whole cluster.</summary>
public sealed class PostgreSqlSource(PostgreSqlSourceOptions options) : IBackupSource
{
    public async Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string?> { ["PGPASSWORD"] = options.Password };
        var connection = new List<string> { "-h", options.Host, "-p", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", options.UserName, "--no-password" };
        var databases = ExternalTool.SplitList(options.Databases);
        var entries = new List<ArchiveEntry>();

        if (databases.Count == 0)
        {
            var file = Path.Combine(context.StagingDirectory, "postgresql_all.sql");
            context.Log.Info("Running pg_dumpall (whole cluster).");
            await ExternalTool.RunAndCheckAsync(ToolPath("pg_dumpall"), [.. connection, "-f", file], environment, "pg_dumpall", file, cancellationToken);
            entries.Add(new ArchiveEntry(file, "postgresql/all.sql"));
        }
        else
        {
            foreach (var database in databases)
            {
                var slug = Slug.From(database, "db");
                var file = Path.Combine(context.StagingDirectory, $"postgresql_{slug}.dump");
                context.Log.Info($"Running pg_dump for '{database}'.");
                await ExternalTool.RunAndCheckAsync(ToolPath("pg_dump"), [.. connection, "-Fc", "-f", file, database], environment, "pg_dump", file, cancellationToken);
                entries.Add(new ArchiveEntry(file, $"postgresql/{slug}.dump"));
            }
        }

        return new SourceSnapshot(entries);
    }

    /// <summary>PgDumpPath may be a folder or the pg_dump executable; pg_dumpall lives next to it.</summary>
    internal string ToolPath(string tool)
    {
        var configured = options.PgDumpPath?.Trim();
        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        if (string.IsNullOrEmpty(configured))
        {
            return tool;
        }

        if (Directory.Exists(configured))
        {
            return Path.Combine(configured, exe);
        }

        var name = Path.GetFileNameWithoutExtension(configured);
        return string.Equals(name, tool, StringComparison.OrdinalIgnoreCase)
            ? configured
            : Path.Combine(Path.GetDirectoryName(configured) ?? string.Empty, exe);
    }
}
