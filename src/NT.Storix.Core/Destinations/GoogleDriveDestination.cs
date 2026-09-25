using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using NT.Storix.Core.Models;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace NT.Storix.Core.Destinations;

/// <summary>
/// Google Drive destination authenticated with a service account. Uses the resumable upload protocol,
/// so interrupted chunks are retried without re-sending the whole file.
/// </summary>
public sealed class GoogleDriveDestination(GoogleDriveOptions options) : IBackupDestination
{
    private DriveService? _service;

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        var service = GetService();
        var request = service.Files.Get(FolderId);
        request.SupportsAllDrives = true;
        request.Fields = "id, mimeType";
        var folder = await request.ExecuteAsync(cancellationToken);
        if (folder.MimeType != "application/vnd.google-apps.folder")
        {
            throw new InvalidOperationException("The configured Google Drive id is not a folder.");
        }
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken)
    {
        var files = await QueryAsync($"'{Escape(FolderId)}' in parents and trashed = false", cancellationToken);
        return files
            .Select(f => new RemoteFile(f.Name, f.Size ?? 0, f.CreatedTimeDateTimeOffset))
            .ToList();
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var service = GetService();

        // Remove leftovers with the same name (Drive allows duplicate names).
        await DeleteAsync(remoteName, cancellationToken);

        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        var metadata = new DriveFile { Name = remoteName, Parents = [FolderId] };
        var request = service.Files.Create(metadata, stream, "application/octet-stream");
        request.SupportsAllDrives = true;
        request.Fields = "id, size";
        request.ChunkSize = Math.Max(1, options.ChunkSizeMb) * 1024 * 1024 / ResumableUpload.MinimumChunkSize * ResumableUpload.MinimumChunkSize;
        if (progress is not null)
        {
            request.ProgressChanged += p => progress.Report(p.BytesSent);
        }

        var result = await request.UploadAsync(cancellationToken);
        if (result.Status != UploadStatus.Completed)
        {
            throw new IOException($"Google Drive upload of '{remoteName}' failed.", result.Exception);
        }
    }

    public async Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var service = GetService();
        var file = (await QueryAsync($"'{Escape(FolderId)}' in parents and name = '{Escape(remoteName)}' and trashed = false", cancellationToken)).FirstOrDefault()
                   ?? throw new FileNotFoundException($"'{remoteName}' was not found in the Google Drive folder.");

        var request = service.Files.Get(file.Id);
        request.SupportsAllDrives = true;
        if (progress is not null)
        {
            request.MediaDownloader.ProgressChanged += p => progress.Report(p.BytesDownloaded);
        }

        await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        var result = await request.DownloadAsync(output, cancellationToken);
        if (result.Status != Google.Apis.Download.DownloadStatus.Completed)
        {
            throw new IOException($"Google Drive download of '{remoteName}' failed.", result.Exception);
        }
    }

    public async Task DeleteAsync(string remoteName, CancellationToken cancellationToken)
    {
        var service = GetService();
        var files = await QueryAsync($"'{Escape(FolderId)}' in parents and name = '{Escape(remoteName)}' and trashed = false", cancellationToken);
        foreach (var file in files)
        {
            var request = service.Files.Delete(file.Id);
            request.SupportsAllDrives = true;
            await request.ExecuteAsync(cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        _service?.Dispose();
        _service = null;
        return ValueTask.CompletedTask;
    }

    private string FolderId => string.IsNullOrWhiteSpace(options.FolderId)
        ? throw new InvalidOperationException("Google Drive folder id is not configured.")
        : options.FolderId.Trim();

    private async Task<List<DriveFile>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        var service = GetService();
        var result = new List<DriveFile>();
        string? pageToken = null;
        do
        {
            var request = service.Files.List();
            request.Q = query;
            request.Fields = "nextPageToken, files(id, name, size, createdTime)";
            request.PageSize = 1000;
            request.SupportsAllDrives = true;
            request.IncludeItemsFromAllDrives = true;
            request.PageToken = pageToken;

            var page = await request.ExecuteAsync(cancellationToken);
            result.AddRange(page.Files ?? []);
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        return result;
    }

    private DriveService GetService()
    {
        if (_service is not null)
        {
            return _service;
        }

        if (string.IsNullOrWhiteSpace(options.ServiceAccountKeyPath) || !File.Exists(options.ServiceAccountKeyPath))
        {
            throw new InvalidOperationException("Google Drive service account key file was not found.");
        }

        var credential = CredentialFactory
            .FromFile<ServiceAccountCredential>(options.ServiceAccountKeyPath)
            .ToGoogleCredential()
            .CreateScoped(DriveService.ScopeConstants.Drive);

        _service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Storix",
        });
        return _service;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
