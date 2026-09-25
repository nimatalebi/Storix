using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Security;

namespace NT.Storix.Core.Destinations;

/// <summary>OneDrive / OneDrive for Business / SharePoint via Microsoft Graph (resumable upload sessions).</summary>
public sealed class OneDriveDestination(OneDriveOptions options, Guid destinationId, int maxUploadKBps = 0, HttpClient? http = null) : IBackupDestination
{
    public const string Scope = "Files.ReadWrite offline_access User.Read";
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private const int ChunkSize = 60 * 320 * 1024; // Must be a multiple of 320 KiB.

    private readonly HttpClient _http = http ?? SharedHttp.Client;
    private OAuthTokens? _token;

    public static string TokenEndpoint(string tenant) => $"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(tenant) ? "common" : tenant.Trim())}/oauth2/v2.0/token";

    public static Task<OAuthTokens> SignInAsync(string clientId, string tenant, CancellationToken cancellationToken) =>
        OAuthLoopback.AuthorizeAsync($"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(tenant) ? "common" : tenant.Trim())}/oauth2/v2.0/authorize",
            TokenEndpoint(tenant), clientId, Scope, null, SharedHttp.Client, cancellationToken);

    private string FolderPath => string.Join('/', (options.Folder ?? string.Empty).Split('/', '\\').Where(p => p.Length > 0).Select(Uri.EscapeDataString));

    private string ItemUrl(string name) => $"{Graph}/me/drive/root:/{FolderPath}/{Uri.EscapeDataString(name)}";

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        using var drive = await SendAsync(HttpMethod.Get, $"{Graph}/me/drive", null, cancellationToken);
        await ListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<RemoteFile>();
        string? url = $"{Graph}/me/drive/root:/{FolderPath}:/children?$select=name,size,lastModifiedDateTime,file&$top=999";
        while (url is not null)
        {
            using var response = await SendAsync(HttpMethod.Get, url, null, cancellationToken, allowNotFound: true);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return result;
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
            {
                if (item.TryGetProperty("file", out _))
                {
                    result.Add(new RemoteFile(
                        item.GetProperty("name").GetString()!,
                        item.GetProperty("size").GetInt64(),
                        DateTimeOffset.Parse(item.GetProperty("lastModifiedDateTime").GetString()!, CultureInfo.InvariantCulture)));
                }
            }

            url = json.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return result;
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var session = await SendAsync(HttpMethod.Post, ItemUrl(remoteName) + ":/createUploadSession",
            new StringContent("""{"item":{"@microsoft.graph.conflictBehavior":"replace"}}""", System.Text.Encoding.UTF8, "application/json"), cancellationToken);
        using var sessionJson = await JsonDocument.ParseAsync(await session.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var uploadUrl = sessionJson.RootElement.GetProperty("uploadUrl").GetString()!;

        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamCopy.BufferSize, useAsync: true);
        await using var stream = ThrottledStream.Wrap(file, maxUploadKBps);
        var total = file.Length;
        var buffer = new byte[ChunkSize];
        long offset = 0;
        do
        {
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
            using var chunk = new ByteArrayContent(buffer, 0, read);
            chunk.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + read - 1, total);

            // The upload URL is pre-authenticated: no Authorization header.
            using var response = await _http.PutAsync(uploadUrl, chunk, cancellationToken);
            await EnsureAsync(response);
            offset += read;
            progress?.Report(offset);
        }
        while (offset < total);
    }

    public async Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, ItemUrl(remoteName) + ":/content", null, cancellationToken);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamCopy.BufferSize, useAsync: true);
        await StreamCopy.CopyAsync(input, output, progress, cancellationToken);
    }

    public async Task DeleteAsync(string remoteName, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, ItemUrl(remoteName), null, cancellationToken, allowNotFound: true);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is null || _token.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            var key = $"onedrive:{destinationId}";
            var refresh = OAuthTokenStore.Load(key) ?? options.RefreshToken;
            if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(refresh))
            {
                throw new InvalidOperationException("OneDrive is not connected. Use 'Sign in' in the destination settings.");
            }

            _token = await OAuthLoopback.RefreshAsync(_http, TokenEndpoint(options.Tenant), options.ClientId.Trim(), refresh, Scope, cancellationToken);

            // Microsoft rotates refresh tokens: keep the newest one.
            if (_token.RefreshToken is { } rotated && rotated != refresh)
            {
                OAuthTokenStore.Save(key, rotated);
            }
        }

        return _token.AccessToken;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancellationToken));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            return response;
        }

        await EnsureAsync(response);
        return response;
    }

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"OneDrive request failed ({(int)response.StatusCode}): {(text.Length > 300 ? text[..300] : text)}", null, response.StatusCode);
        }
    }
}
