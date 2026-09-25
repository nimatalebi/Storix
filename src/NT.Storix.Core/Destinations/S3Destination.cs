using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Destinations;

/// <summary>
/// Amazon S3 and S3-compatible storage. Large files use multipart uploads; an object only becomes
/// visible when the upload completes, and abandoned multipart uploads are aborted before retrying.
/// </summary>
public sealed class S3Destination(S3Options options) : IBackupDestination
{
    private AmazonS3Client? _client;

    private string Bucket => string.IsNullOrWhiteSpace(options.BucketName)
        ? throw new InvalidOperationException("S3 bucket name is not configured.")
        : options.BucketName.Trim();

    private string KeyPrefix => string.IsNullOrWhiteSpace(options.Prefix) ? string.Empty : options.Prefix.Trim().Trim('/') + "/";

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        var client = GetClient();
        await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Prefix = KeyPrefix, MaxKeys = 1 }, cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken)
    {
        var client = GetClient();
        var result = new List<RemoteFile>();
        var request = new ListObjectsV2Request { BucketName = Bucket, Prefix = KeyPrefix, Delimiter = "/" };
        ListObjectsV2Response response;
        do
        {
            response = await client.ListObjectsV2Async(request, cancellationToken);
            foreach (var item in response.S3Objects ?? [])
            {
                var name = item.Key[KeyPrefix.Length..];
                if (name.Length > 0)
                {
                    result.Add(new RemoteFile(name, item.Size ?? 0, item.LastModified is { } modified ? new DateTimeOffset(modified.ToUniversalTime()) : null));
                }
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return result;
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var client = GetClient();
        var key = KeyPrefix + remoteName;

        // Partial upload cleanup: abort multipart uploads left by an interrupted attempt for this key.
        await AbortIncompleteUploadsAsync(client, key, cancellationToken);

        var request = new TransferUtilityUploadRequest
        {
            BucketName = Bucket,
            Key = key,
            FilePath = localPath,
            PartSize = Math.Clamp(options.PartSizeMb, 5, 512) * 1024L * 1024L,
        };
        if (!string.IsNullOrWhiteSpace(options.StorageClass))
        {
            request.StorageClass = S3StorageClass.FindValue(options.StorageClass.Trim());
        }

        if (progress is not null)
        {
            request.UploadProgressEvent += (_, e) => progress.Report(e.TransferredBytes);
        }

        using var transfer = new TransferUtility(client);
        await transfer.UploadAsync(request, cancellationToken);
    }

    public async Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var client = GetClient();
        using var response = await client.GetObjectAsync(Bucket, KeyPrefix + remoteName, cancellationToken);
        await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        await Processing.StreamCopy.CopyAsync(response.ResponseStream, output, progress, cancellationToken);
    }

    public async Task DeleteAsync(string remoteName, CancellationToken cancellationToken)
    {
        await GetClient().DeleteObjectAsync(Bucket, KeyPrefix + remoteName, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }

    private async Task AbortIncompleteUploadsAsync(AmazonS3Client client, string key, CancellationToken cancellationToken)
    {
        try
        {
            var uploads = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket, Prefix = key }, cancellationToken);
            foreach (var upload in (uploads.MultipartUploads ?? []).Where(u => u.Key == key))
            {
                await client.AbortMultipartUploadAsync(Bucket, key, upload.UploadId, cancellationToken);
            }
        }
        catch (AmazonS3Exception)
        {
            // Some S3-compatible servers do not implement listing multipart uploads.
        }
    }

    private AmazonS3Client GetClient()
    {
        if (_client is not null)
        {
            return _client;
        }

        if (string.IsNullOrWhiteSpace(options.AccessKeyId) || string.IsNullOrEmpty(options.SecretAccessKey))
        {
            throw new InvalidOperationException("S3 access key and secret key are required.");
        }

        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,

            // Compatibility with S3-compatible servers that do not support the newer flexible checksums.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        if (string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(string.IsNullOrWhiteSpace(options.Region) ? "us-east-1" : options.Region.Trim());
        }
        else
        {
            config.ServiceURL = options.ServiceUrl.Trim();
            config.AuthenticationRegion = string.IsNullOrWhiteSpace(options.Region) ? "us-east-1" : options.Region.Trim();
        }

        _client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKeyId.Trim(), options.SecretAccessKey), config);
        return _client;
    }
}
