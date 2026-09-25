using Microsoft.Data.Sqlite;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <summary>SQLite databases copied with the online backup API (consistent even while the database is in use).</summary>
public sealed class SqliteSource(SqliteSourceOptions options) : IBackupSource
{
    public Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var paths = ExternalTool.SplitList(options.DatabasePaths);
        if (paths.Count == 0)
        {
            throw new InvalidOperationException("No SQLite database selected.");
        }

        var entries = new List<ArchiveEntry>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"SQLite database not found: {path}", path);
            }

            var name = Path.GetFileName(path);
            for (var i = 2; !used.Add(name); i++)
            {
                name = $"{Path.GetFileNameWithoutExtension(path)}_{i}{Path.GetExtension(path)}";
            }

            var copy = Path.Combine(context.StagingDirectory, "sqlite_" + name);
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
            }

            context.Log.Info($"Copied SQLite database {path}.");
            entries.Add(new ArchiveEntry(copy, $"sqlite/{name}"));
        }

        return Task.FromResult(new SourceSnapshot(entries));
    }
}
