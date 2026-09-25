using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <summary>Docker volumes archived as tar files by a short-lived helper container.</summary>
public sealed class DockerVolumesSource(DockerVolumesSourceOptions options) : IBackupSource
{
    public async Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var volumes = ExternalTool.SplitList(options.Volumes);
        if (volumes.Count == 0)
        {
            throw new InvalidOperationException("No Docker volume selected.");
        }

        var docker = ExternalTool.Tool(options.DockerPath, "docker");
        var stop = ExternalTool.SplitList(options.StopContainers);
        var target = Path.Combine(context.StagingDirectory, "docker");
        Directory.CreateDirectory(target);
        var entries = new List<ArchiveEntry>();

        if (stop.Count > 0)
        {
            context.Log.Info($"Stopping container(s): {string.Join(", ", stop)}.");
            await ExternalTool.RunAndCheckAsync(docker, ["stop", .. stop], null, "docker stop", null, cancellationToken);
        }

        try
        {
            foreach (var volume in volumes)
            {
                var file = $"{Slug.From(volume, "volume")}.tar";
                context.Log.Info($"Archiving Docker volume '{volume}'.");
                await ExternalTool.RunAndCheckAsync(docker,
                    ["run", "--rm", "-v", $"{volume}:/source:ro", "-v", $"{target}:/backup", options.HelperImage, "tar", "-cf", $"/backup/{file}", "-C", "/source", "."],
                    null, "docker run", Path.Combine(target, file), cancellationToken);
                entries.Add(new ArchiveEntry(Path.Combine(target, file), $"docker/{file}"));
            }
        }
        finally
        {
            if (stop.Count > 0)
            {
                context.Log.Info($"Starting container(s): {string.Join(", ", stop)}.");
                await ExternalTool.RunAsync(docker, ["start", .. stop], null, "docker start", CancellationToken.None);
            }
        }

        return new SourceSnapshot(entries);
    }
}
