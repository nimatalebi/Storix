using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Destinations;

/// <summary>
/// Azure Blob Storage. Large files are uploaded as blocks and committed at the end, so a blob only appears
/// when it is complete.
/// </summary>
public sealed class AzureBlobDestination(AzureBlobOptions options, int maxUploadKBps = 0) : IBackupDestination
{
    private BlobContainerClient? _container;

    private string Prefix => string.IsNullOrWhiteSpace(options.Prefix) ? string.Empty : options.Prefix.Trim().Trim('/') + "/";

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        await Container().CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<RemoteFile>();
        try
        {
            await foreach (var item in Container().GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", Prefix, cancellationToken))
            {
                if (item.IsBlob)
                {
                    result.Add(new RemoteFile(item.Blob.Name[Prefix.Length..], item.Blob.Properties.ContentLength ?? 0, item.Blob.Properties.LastModified));
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Container does not exist yet.
        }

        return result;
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await Container().CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blob = Container().GetBlobClient(Prefix + remoteName);

        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamCopy.BufferSize, useAsync: true);
        await using var stream = ThrottledStream.Wrap(file, maxUploadKBps);
        var upload = new BlobUploadOptions
        {
            TransferOptions = new StorageTransferOptions { MaximumTransferSize = 8 * 1024 * 1024, InitialTransferSize = 8 * 1024 * 1024 },
            ProgressHandler = progress,
        };
        if (!string.IsNullOrWhiteSpace(options.AccessTier))
        {
            upload.AccessTier = new AccessTier(options.AccessTier.Trim());
        }

        await blob.UploadAsync(stream, upload, cancellationToken);
    }

    public async Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await Container().GetBlobClient(Prefix + remoteName).DownloadToAsync(localPath, cancellationToken);
    }

    public async Task DeleteAsync(string remoteName, CancellationToken cancellationToken)
    {
        await Container().GetBlobClient(Prefix + remoteName).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private BlobContainerClient Container()
    {
        if (_container is not null)
        {
            return _container;
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("Azure storage connection string is not configured.");
        }

        _container = new BlobServiceClient(options.ConnectionString).GetBlobContainerClient(options.Container.Trim());
        return _container;
    }
}
