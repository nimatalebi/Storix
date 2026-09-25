using FluentFTP;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Destinations;

public sealed class FtpDestination(FtpOptions options) : IBackupDestination
{
    private AsyncFtpClient? _client;

    private string RemoteDirectory => string.IsNullOrWhiteSpace(options.RemotePath) ? "/" : options.RemotePath.TrimEnd('/') + "/";

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        await client.CreateDirectory(RemoteDirectory, true, cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        if (!await client.DirectoryExists(RemoteDirectory, cancellationToken))
        {
            return [];
        }

        var items = await client.GetListing(RemoteDirectory, cancellationToken);
        return items
            .Where(i => i.Type == FtpObjectType.File)
            .Select(i => new RemoteFile(i.Name, i.Size, i.Modified == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(i.Modified, DateTimeKind.Utc))))
            .ToList();
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        var target = RemoteDirectory + remoteName;
        var partial = target + BackupNaming.PartialSuffix;

        IProgress<FtpProgress>? ftpProgress = progress is null ? null : new Progress<FtpProgress>(p => progress.Report(p.TransferredBytes));

        // FtpRemoteExists.Resume continues a previously interrupted transfer of the partial file.
        var status = await client.UploadFile(localPath, partial, FtpRemoteExists.Resume, createRemoteDir: true, FtpVerify.None, ftpProgress, cancellationToken);
        if (status == FtpStatus.Failed)
        {
            throw new IOException($"FTP upload of '{remoteName}' failed.");
        }

        var remoteSize = await client.GetFileSize(partial, -1, cancellationToken);
        var localSize = new FileInfo(localPath).Length;
        if (remoteSize != localSize)
        {
            // Corrupt or mismatching partial file: remove it so the next attempt starts from scratch.
            await client.DeleteFile(partial, cancellationToken);
            throw new IOException($"FTP upload size mismatch for '{remoteName}' ({remoteSize} of {localSize} bytes).");
        }

        await client.MoveFile(partial, target, FtpRemoteExists.Overwrite, cancellationToken);
    }

    public async Task DeleteAsync(string remoteName, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        var path = RemoteDirectory + remoteName;
        if (await client.FileExists(path, cancellationToken))
        {
            await client.DeleteFile(path, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            try
            {
                await _client.Disconnect();
            }
            catch
            {
                // Best effort.
            }

            _client.Dispose();
            _client = null;
        }
    }

    private async Task<AsyncFtpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true })
        {
            return _client;
        }

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new InvalidOperationException("FTP host is not configured.");
        }

        _client?.Dispose();
        var client = new AsyncFtpClient(options.Host, options.UserName, options.Password ?? string.Empty, options.Port);
        client.Config.EncryptionMode = options.Encryption switch
        {
            FtpEncryption.Explicit => FtpEncryptionMode.Explicit,
            FtpEncryption.Implicit => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.None,
        };
        client.Config.ValidateAnyCertificate = options.AcceptAnyCertificate;
        client.Config.DataConnectionType = options.Passive ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive;
        client.Config.RetryAttempts = 1;

        await client.Connect(cancellationToken);
        _client = client;
        return client;
    }
}
