using System.Diagnostics;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <summary>Dumps MongoDB with the official <c>mongodump</c> tool into a single archive file.</summary>
public sealed class MongoDbSource(MongoDbSourceOptions options) : IBackupSource
{
    public async Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("MongoDB connection string is not configured.");
        }

        var name = string.IsNullOrWhiteSpace(options.Database) ? "all" : Slug.From(options.Database, "db");
        var archivePath = Path.Combine(context.StagingDirectory, $"mongodb_{name}.archive");

        var start = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(options.MongodumpPath) ? "mongodump" : options.MongodumpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add($"--uri={options.ConnectionString}");
        start.ArgumentList.Add($"--archive={archivePath}");
        if (!string.IsNullOrWhiteSpace(options.Database))
        {
            start.ArgumentList.Add($"--db={options.Database.Trim()}");
        }
        else if (options.UseOplog)
        {
            start.ArgumentList.Add("--oplog");
        }

        foreach (var argument in SplitArguments(options.ExtraArguments))
        {
            start.ArgumentList.Add(argument);
        }

        context.Log.Info($"Running mongodump ({(string.IsNullOrWhiteSpace(options.Database) ? "all databases" : options.Database)}).");

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("mongodump was not found. Install MongoDB Database Tools or set the mongodump path.", ex);
        }

        // mongodump reports progress on stderr.
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = (await stdout + await stderr).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"mongodump exited with code {process.ExitCode}: {Tail(output)}");
        }

        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length == 0)
        {
            throw new InvalidOperationException("mongodump did not produce an archive.");
        }

        context.Log.Info($"mongodump finished ({new FileInfo(archivePath).Length:N0} bytes).");
        return new SourceSnapshot([new ArchiveEntry(archivePath, $"mongodb/{Path.GetFileName(archivePath)}")]);
    }

    private static string Tail(string text, int max = 2000) => text.Length <= max ? text : "..." + text[^max..];

    internal static IEnumerable<string> SplitArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            yield break;
        }

        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
