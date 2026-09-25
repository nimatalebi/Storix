using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.Core.Plugins;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Sources;

namespace NT.Storix.Plugins.Sample;

/// <summary>
/// Example source plugin: writes a text file (e.g. an inventory) into the backup. A real plugin would export
/// data from an application here; files written to the staging folder are removed after the run.
/// </summary>
public sealed class TextSourcePlugin : ISourcePlugin
{
    public string Id => "sample.text";

    public string DisplayName => "Sample: text file";

    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("FileName", "Name of the file in the backup", Required: true),
        new("Content", "Text written into the file"),
    ];

    public IBackupSource Create(PluginContext context) => new Source(context.Require("FileName"), context.Get("Content") ?? string.Empty);

    private sealed class Source(string fileName, string content) : IBackupSource
    {
        public async Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken)
        {
            var path = Path.Combine(context.StagingDirectory, Path.GetFileName(fileName));
            await File.WriteAllTextAsync(path, content, cancellationToken);
            context.Log.Info($"Sample plugin wrote {fileName}.");
            return new SourceSnapshot([new ArchiveEntry(path, "sample/" + Path.GetFileName(fileName)) { DeleteAfterRun = true }]);
        }
    }
}

/// <summary>
/// Example destination plugin: stores backups in a folder, reusing the built-in local folder destination.
/// A real plugin implements <see cref="IBackupDestination"/> against its own storage API.
/// </summary>
public sealed class FolderDestinationPlugin : IDestinationPlugin
{
    public string Id => "sample.folder";

    public string DisplayName => "Sample: folder";

    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("Path", "Target folder", Required: true),
        new("Token", "Example of a secret setting (not used)", Secret: true),
    ];

    public IBackupDestination Create(PluginContext context) =>
        new LocalFolderDestination(new LocalFolderOptions { Path = context.Require("Path") }, context.MaxUploadKBps);
}
