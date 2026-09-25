using System.Globalization;
using System.Text.Json;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Destinations;

/// <summary>Any storage supported by rclone (Backblaze B2, Box, pCloud, Mega, SharePoint, Swift, ...).</summary>
public sealed class RcloneDestination(RcloneOptions options, int maxUploadKBps = 0) : IBackupDestination
{
    private string Remote => string.IsNullOrWhiteSpace(options.Remote)
        ? throw new InvalidOperationException("rclone remote is not configured (e.g. b2:bucket/backups).")
        : options.Remote.Trim().TrimEnd('/');

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        await RunAsync(["mkdir", Remote], cancellationToken);
        await ListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await ExternalTool.RunAsync(Tool, [.. Common, "lsjson", "--files-only", Remote], null, "rclone", cancellationToken);
        if (result.ExitCode != 0)
        {
            if (result.Output.Contains("directory not found", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            throw new InvalidOperationException($"rclone lsjson failed: {result.Output}");
        }

        // lsjson writes JSON to stdout; the captured output may also contain log lines on stderr.
        var json = result.Output[result.Output.IndexOf('[', StringComparison.Ordinal)..];
        using var document = JsonDocument.Parse(json[..(json.LastIndexOf(']') + 1)]);
        return document.RootElement.EnumerateArray()
            .Select(e => new RemoteFile(
                e.GetProperty("Name").GetString()!,
                e.GetProperty("Size").GetInt64(),
                DateTimeOffset.TryParse(e.GetProperty("ModTime").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null))
            .ToList();
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var partial = $"{Remote}/{remoteName}{BackupNaming.PartialSuffix}";
        var limit = maxUploadKBps > 0 ? new[] { "--bwlimit", $"{maxUploadKBps}K" } : [];
        await RunAsync(["copyto", .. limit, localPath, partial], cancellationToken);
        await RunAsync(["moveto", partial, $"{Remote}/{remoteName}"], cancellationToken);
    }

    public Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken) =>
        RunAsync(["copyto", $"{Remote}/{remoteName}", localPath], cancellationToken);

    public async Task DeleteAsync(string remoteName, CancellationToken cancellationToken)
    {
        var result = await ExternalTool.RunAsync(Tool, [.. Common, "deletefile", $"{Remote}/{remoteName}"], null, "rclone", cancellationToken);
        if (result.ExitCode != 0 && !result.Output.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"rclone deletefile failed: {result.Output}");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string Tool => ExternalTool.Tool(options.RclonePath, "rclone");

    private IEnumerable<string> Common
    {
        get
        {
            var arguments = new List<string>();
            if (!string.IsNullOrWhiteSpace(options.ConfigPath))
            {
                arguments.AddRange(["--config", options.ConfigPath.Trim()]);
            }

            arguments.AddRange(MongoDbSource.SplitArguments(options.ExtraArguments));
            return arguments;
        }
    }

    private async Task RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var args = Common.Concat(arguments).ToList();
        var result = await ExternalTool.RunAsync(Tool, args, null, "rclone", cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"rclone {args.First(a => !a.StartsWith('-'))} failed: {result.Output}");
        }
    }
}
