using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <summary>Redis: RDB snapshot through <c>redis-cli --rdb</c>.</summary>
public sealed class RedisSource(RedisSourceOptions options) : IBackupSource
{
    public async Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var file = Path.Combine(context.StagingDirectory, "redis_dump.rdb");
        var arguments = new List<string> { "-h", options.Host, "-p", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            arguments.AddRange(["--user", options.UserName]);
        }

        arguments.AddRange(["--rdb", file]);
        context.Log.Info("Running redis-cli --rdb.");

        // REDISCLI_AUTH keeps the password off the command line.
        await ExternalTool.RunAndCheckAsync(ExternalTool.Tool(options.RedisCliPath, "redis-cli"), arguments,
            new Dictionary<string, string?> { ["REDISCLI_AUTH"] = options.Password }, "redis-cli", file, cancellationToken);

        return new SourceSnapshot([new ArchiveEntry(file, "redis/dump.rdb")]);
    }
}
