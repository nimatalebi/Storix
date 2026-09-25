using System.Diagnostics;

namespace NT.Storix.Core.Sources;

/// <summary>Restores a <c>mongodump</c> archive with the official <c>mongorestore</c> tool.</summary>
public static class MongoDbRestorer
{
    /// <param name="mongorestorePath">Path to mongorestore; empty = on PATH.</param>
    /// <param name="archivePath">The <c>.archive</c> file produced by Storix.</param>
    /// <param name="fromDatabase">Restore only this database (optional).</param>
    /// <param name="toDatabase">Rename <paramref name="fromDatabase"/> to this database (optional).</param>
    /// <param name="drop">Drop existing collections before restoring.</param>
    public static async Task RestoreAsync(
        string? mongorestorePath,
        string connectionString,
        string archivePath,
        string? fromDatabase,
        string? toDatabase,
        bool drop,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(mongorestorePath) ? "mongorestore" : mongorestorePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add($"--uri={connectionString}");
        start.ArgumentList.Add($"--archive={archivePath}");
        if (drop)
        {
            start.ArgumentList.Add("--drop");
        }

        if (!string.IsNullOrWhiteSpace(fromDatabase))
        {
            start.ArgumentList.Add($"--nsInclude={fromDatabase}.*");
            if (!string.IsNullOrWhiteSpace(toDatabase))
            {
                start.ArgumentList.Add($"--nsFrom={fromDatabase}.*");
                start.ArgumentList.Add($"--nsTo={toDatabase}.*");
            }
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("mongorestore was not found. Install MongoDB Database Tools or set its path.", ex);
        }

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
            throw new InvalidOperationException($"mongorestore exited with code {process.ExitCode}: {(output.Length > 2000 ? "..." + output[^2000..] : output)}");
        }
    }
}
