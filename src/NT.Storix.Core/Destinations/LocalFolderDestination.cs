using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Destinations;

public sealed class LocalFolderDestination(LocalFolderOptions options) : IBackupDestination
{
    private const int BufferSize = 1024 * 1024;

    private string Root => string.IsNullOrWhiteSpace(options.Path)
        ? throw new InvalidOperationException("Local folder path is not configured.")
        : options.Path;

    public Task TestAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        var probe = Path.Combine(Root, $".storix-probe-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Root))
        {
            return Task.FromResult<IReadOnlyList<RemoteFile>>([]);
        }

        IReadOnlyList<RemoteFile> files = new DirectoryInfo(Root)
            .EnumerateFiles()
            .Select(f => new RemoteFile(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
        return Task.FromResult(files);
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        var target = Path.Combine(Root, remoteName);
        var partial = target + BackupNaming.PartialSuffix;

        await using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
        {
            // Resume an interrupted copy when the partial file is a prefix of the source.
            var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (existing > input.Length)
            {
                File.Delete(partial);
                existing = 0;
            }

            await using var output = new FileStream(partial, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            input.Position = existing;

            await StreamCopy.CopyAsync(input, output, progress, cancellationToken, existing);
            await output.FlushAsync(cancellationToken);
        }

        File.Move(partial, target, overwrite: true);
    }

    public async Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var source = Path.Combine(Root, remoteName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"'{remoteName}' was not found in '{Root}'.", source);
        }

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        await StreamCopy.CopyAsync(input, output, progress, cancellationToken);
    }

    public Task DeleteAsync(string remoteName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Root, remoteName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
