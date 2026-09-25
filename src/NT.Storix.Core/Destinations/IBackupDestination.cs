namespace NT.Storix.Core.Destinations;

public sealed record RemoteFile(string Name, long Size, DateTimeOffset? LastModified);

/// <summary>A backup (archive plus related files such as the checksum sidecar) stored on a destination.</summary>
public sealed record BackupFileInfo(string Name, DateTimeOffset CreatedAt, IReadOnlyList<string> Files);

/// <summary>A place backups are uploaded to. Instances are short-lived: one per job run.</summary>
public interface IBackupDestination : IAsyncDisposable
{
    /// <summary>Connects and makes sure the target folder is reachable (creating it if needed).</summary>
    Task TestAsync(CancellationToken cancellationToken);

    /// <summary>Lists files in the target folder (non-recursive).</summary>
    Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Uploads a file. Implementations upload to a temporary name first and rename on completion, and resume
    /// an interrupted upload of the same file when the protocol allows it.
    /// </summary>
    Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken);

    /// <summary>Downloads a remote file to <paramref name="localPath"/> (overwriting it).</summary>
    Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken);

    Task DeleteAsync(string remoteName, CancellationToken cancellationToken);
}
