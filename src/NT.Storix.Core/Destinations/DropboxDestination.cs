using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Security;

namespace NT.Storix.Core.Destinations;

/// <summary>Dropbox (upload sessions, so files of any size are uploaded in chunks).</summary>
public sealed class DropboxDestination(DropboxOptions options, int maxUploadKBps = 0, HttpClient? http = null) : IBackupDestination
{
    public const string AuthorizeEndpoint = "https://www.dropbox.com/oauth2/authorize";
    public const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";
    private const string Api = "https://api.dropboxapi.com/2/";
    private const string Content = "https://content.dropboxapi.com/2/";
    private const int ChunkSize = 8 * 1024 * 1024;

    private readonly HttpClient _http = http ?? SharedHttp.Client;
    private OAuthTokens? _token;

    private string Folder => "/" + (options.Folder ?? string.Empty).Trim().Trim('/');

    private string PathOf(string name) => Folder.TrimEnd('/') + "/" + name;

    public static Task<OAuthTokens> SignInAsync(string appKey, CancellationToken cancellationToken) =>
        OAuthLoopback.AuthorizeAsync(AuthorizeEndpoint, TokenEndpoint, appKey, null,
            new Dictionary<string, string> { ["token_access_type"] = "offline" }, SharedHttp.Client, cancellationToken);

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        using var response = await PostJsonAsync(Api + "files/create_folder_v2", new { path = Folder, autorename = false }, cancellationToken, allowConflict: true);
        await ListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<RemoteFile>();
        using var first = await PostJsonAsync(Api + "files/list_folder", new { path = Folder }, cancellationToken, allowNotFound: true);
        if (first.StatusCode == HttpStatusCode.Conflict)
        {
            return result;
        }

        var page = await ReadJsonAsync(first, cancellationToken);
        while (true)
        {
            foreach (var entry in page.RootElement.GetProperty("entries").EnumerateArray())
            {
                if (entry.GetProperty(".tag").GetString() == "file")
                {
                    result.Add(new RemoteFile(
                        entry.GetProperty("name").GetString()!,
                        entry.GetProperty("size").GetInt64(),
                        DateTimeOffset.Parse(entry.GetProperty("server_modified").GetString()!, CultureInfo.InvariantCulture)));
                }
            }

            if (!page.RootElement.GetProperty("has_more").GetBoolean())
            {
                return result;
            }

            using var next = await PostJsonAsync(Api + "files/list_folder/continue", new { cursor = page.RootElement.GetProperty("cursor").GetString() }, cancellationToken);
            page = await ReadJsonAsync(next, cancellationToken);
        }
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamCopy.BufferSize, useAsync: true);
        await using var stream = ThrottledStream.Wrap(file, maxUploadKBps);
        var buffer = new byte[ChunkSize];
        string? session = null;
        long offset = 0;

        while (true)
        {
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
            var chunk = new ReadOnlyMemory<byte>(buffer, 0, read);
            if (session is null)
            {
                using var start = await PostContentAsync("files/upload_session/start", new { close = false }, chunk, cancellationToken);
                session = (await ReadJsonAsync(start, cancellationToken)).RootElement.GetProperty("session_id").GetString();
            }
            else if (read > 0)
            {
                using var append = await PostContentAsync("files/upload_session/append_v2", new { cursor = new { session_id = session, offset }, close = false }, chunk, cancellationToken);
            }

            offset += read;
            progress?.Report(offset);
            if (read < buffer.Length)
            {
                break;
            }
        }

        // The file appears only when the session is committed.
        using var finish = await PostContentAsync("files/upload_session/finish", new
        {
            cursor = new { session_id = session, offset },
            commit = new { path = PathOf(remoteName), mode = "overwrite", mute = true },
        }, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }

    public async Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Content + "files/download");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancellationToken));
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = PathOf(remoteName) }));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureAsync(response);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamCopy.BufferSize, useAsync: true);
        await StreamCopy.CopyAsync(input, output, progress, cancellationToken);
    }

    public async Task DeleteAsync(string remoteName, CancellationToken cancellationToken)
    {
        using var response = await PostJsonAsync(Api + "files/delete_v2", new { path = PathOf(remoteName) }, cancellationToken, allowNotFound: true);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is null || _token.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            if (string.IsNullOrWhiteSpace(options.AppKey) || string.IsNullOrWhiteSpace(options.RefreshToken))
            {
                throw new InvalidOperationException("Dropbox is not connected. Use 'Sign in' in the destination settings.");
            }

            _token = await OAuthLoopback.RefreshAsync(_http, TokenEndpoint, options.AppKey.Trim(), options.RefreshToken, null, cancellationToken);
        }

        return _token.AccessToken;
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string url, object body, CancellationToken cancellationToken, bool allowNotFound = false, bool allowConflict = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancellationToken));
        var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if ((allowNotFound && text.Contains("not_found", StringComparison.Ordinal)) || (allowConflict && text.Contains("conflict", StringComparison.Ordinal)))
            {
                return response;
            }

            response.Dispose();
            throw new HttpRequestException($"Dropbox request failed (409): {text}", null, HttpStatusCode.Conflict);
        }

        await EnsureAsync(response);
        return response;
    }

    private async Task<HttpResponseMessage> PostContentAsync(string endpoint, object argument, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Content + endpoint) { Content = new ReadOnlyMemoryContent(data) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancellationToken));
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(argument));
        var response = await _http.SendAsync(request, cancellationToken);
        await EnsureAsync(response);
        return response;
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Dropbox request failed ({(int)response.StatusCode}): {(text.Length > 300 ? text[..300] : text)}", null, response.StatusCode);
        }
    }
}
