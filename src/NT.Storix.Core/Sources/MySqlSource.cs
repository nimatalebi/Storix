using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <summary>MySQL / MariaDB: mysqldump with routines, triggers and events (consistent with --single-transaction).</summary>
public sealed class MySqlSource(MySqlSourceOptions options) : IBackupSource
{
    public async Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var databases = ExternalTool.SplitList(options.Databases);
        var name = databases.Count == 1 ? Slug.From(databases[0], "db") : "all";
        var file = Path.Combine(context.StagingDirectory, $"mysql_{name}.sql");

        var arguments = new List<string>
        {
            "-h", options.Host,
            "-P", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-u", options.UserName,
            "--routines", "--triggers", "--events", "--hex-blob",
            $"--result-file={file}",
        };
        if (options.SingleTransaction)
        {
            arguments.Add("--single-transaction");
        }

        if (databases.Count == 0)
        {
            arguments.Add("--all-databases");
        }
        else
        {
            arguments.Add("--databases");
            arguments.AddRange(databases);
        }

        context.Log.Info($"Running mysqldump ({(databases.Count == 0 ? "all databases" : string.Join(", ", databases))}).");

        // MYSQL_PWD keeps the password off the command line (also honoured by mariadb-dump).
        await ExternalTool.RunAndCheckAsync(ExternalTool.Tool(options.MysqldumpPath, "mysqldump"), arguments,
            new Dictionary<string, string?> { ["MYSQL_PWD"] = options.Password }, "mysqldump", file, cancellationToken);

        return new SourceSnapshot([new ArchiveEntry(file, $"mysql/{name}.sql")]);
    }
}
