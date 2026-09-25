using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;
using Renci.SshNet;

namespace NT.Storix.Core.Destinations;

public sealed class SftpDestination(SftpOptions options) : IBackupDestination
{
    private const int BufferSize = 256 * 1024;
    private SftpClient? _client;

    private string RemoteDirectory => string.IsNullOrWhiteSpace(options.RemotePath) ? "." : options.RemotePath.TrimEnd('/');

    public Task TestAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var client = GetClient();
        EnsureDirectory(client, RemoteDirectory);
    }, cancellationToken);

    public Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<RemoteFile>>(() =>
    {
        var client = GetClient();
        if (!client.Exists(RemoteDirectory))
        {
            return [];
        }

        return client.ListDirectory(RemoteDirectory)
            .Where(f => f.IsRegularFile)
            .Select(f => new RemoteFile(f.Name, f.Length, new DateTimeOffset(DateTime.SpecifyKind(f.LastWriteTimeUtc, DateTimeKind.Utc))))
            .ToList();
    }, cancellationToken);

    public Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var client = GetClient();
        EnsureDirectory(client, RemoteDirectory);

        var target = $"{RemoteDirectory}/{remoteName}";
        var partial = target + BackupNaming.PartialSuffix;

        using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);

        // Resume: append to an existing partial file if it is not larger than the source.
        long offset = 0;
        if (client.Exists(partial))
        {
            offset = client.GetAttributes(partial).Size;
            if (offset > input.Length)
            {
                client.DeleteFile(partial);
                offset = 0;
            }
        }

        using (var output = client.Open(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write))
        {
            input.Position = offset;
            var buffer = new byte[BufferSize];
            var total = offset;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                total += read;
                progress?.Report(total);
            }

            output.Flush();
        }

        var remoteSize = client.GetAttributes(partial).Size;
        if (remoteSize != input.Length)
        {
            client.DeleteFile(partial);
            throw new IOException($"SFTP upload size mismatch for '{remoteName}' ({remoteSize} of {input.Length} bytes).");
        }

        if (client.Exists(target))
        {
            client.DeleteFile(target);
        }

        client.RenameFile(partial, target);
    }, cancellationToken);

    public Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var client = GetClient();
        using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
        client.DownloadFile($"{RemoteDirectory}/{remoteName}", output, progress is null ? null : bytes => progress.Report((long)bytes));
    }, cancellationToken);

    public Task DeleteAsync(string remoteName, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var client = GetClient();
        var path = $"{RemoteDirectory}/{remoteName}";
        if (client.Exists(path))
        {
            client.DeleteFile(path);
        }
    }, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }

            _client.Dispose();
            _client = null;
        }

        return ValueTask.CompletedTask;
    }

    private SftpClient GetClient()
    {
        if (_client is { IsConnected: true })
        {
            return _client;
        }

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new InvalidOperationException("SFTP host is not configured.");
        }

        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            var key = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                ? new PrivateKeyFile(options.PrivateKeyPath)
                : new PrivateKeyFile(options.PrivateKeyPath, options.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(options.UserName, key));
        }

        if (!string.IsNullOrEmpty(options.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(options.UserName, options.Password));
        }

        if (methods.Count == 0)
        {
            throw new InvalidOperationException("SFTP requires a password or a private key.");
        }

        _client?.Dispose();
        var client = new SftpClient(new ConnectionInfo(options.Host, options.Port, options.UserName, methods.ToArray()));

        if (!string.IsNullOrWhiteSpace(options.HostKeyFingerprint))
        {
            var expected = options.HostKeyFingerprint.Trim().Replace("SHA256:", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('=');
            client.HostKeyReceived += (_, e) => e.CanTrust = string.Equals(e.FingerPrintSHA256.TrimEnd('='), expected, StringComparison.Ordinal);
        }

        client.Connect();
        _client = client;
        return client;
    }

    private static void EnsureDirectory(SftpClient client, string path)
    {
        if (path is "." or "" || client.Exists(path))
        {
            return;
        }

        var current = path.StartsWith('/') ? "/" : string.Empty;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 || current.EndsWith('/') ? current + part : $"{current}/{part}";
            if (!client.Exists(current))
            {
                client.CreateDirectory(current);
            }
        }
    }
}
