using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Destinations;

/// <summary>WebDAV (Nextcloud, ownCloud, Synology/QNAP NAS, IIS, Apache). Uploads to a temporary name and MOVEs it.</summary>
public sealed class WebDavDestination : IBackupDestination
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly WebDavOptions _options;
    private readonly int _maxUploadKBps;
    private readonly HttpClient _http;
    private readonly Uri _folder;

    public WebDavDestination(WebDavOptions options, int maxUploadKBps = 0, HttpMessageHandler? handler = null)
    {
        _options = options;
        _maxUploadKBps = maxUploadKBps;
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            throw new InvalidOperationException("WebDAV URL is not configured.");
        }

        _folder = new Uri(options.Url.TrimEnd('/') + "/");
        handler ??= new SocketsHttpHandler
        {
            SslOptions = options.AcceptAnyCertificate ? new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true } : new(),
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrEmpty(options.UserName))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.UserName}:{options.Password}")));
        }
    }

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        await EnsureFolderAsync(cancellationToken);
        await ListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _folder)
        {
            Content = new StringContent("""<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:prop><d:getcontentlength/><d:getlastmodified/><d:resourcetype/></d:prop></d:propfind>""", Encoding.UTF8, "application/xml"),
        };
        request.Headers.Add("Depth", "1");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        await EnsureSuccessAsync(response, "PROPFIND");
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var files = new List<RemoteFile>();
        foreach (var item in xml.Descendants(Dav + "response"))
        {
            var href = item.Element(Dav + "href")?.Value;
            var prop = item.Descendants(Dav + "prop").FirstOrDefault();
            if (href is null || prop is null || prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(href.TrimEnd('/').Split('/')[^1]);
            long.TryParse(prop.Element(Dav + "getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            DateTimeOffset? modified = DateTimeOffset.TryParse(prop.Element(Dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m : null;
            files.Add(new RemoteFile(name, size, modified));
        }

        return files;
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await EnsureFolderAsync(cancellationToken);
        var partial = FileUri(remoteName + BackupNaming.PartialSuffix);

        await using (var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamCopy.BufferSize, useAsync: true))
        await using (var stream = ThrottledStream.Wrap(file, _maxUploadKBps))
        {
            using var content = new StreamContent(stream, StreamCopy.BufferSize);
            content.Headers.ContentLength = file.Length;
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var put = await _http.PutAsync(partial, content, cancellationToken);
            await EnsureSuccessAsync(put, "PUT");
        }

        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), partial);
        move.Headers.Add("Destination", FileUri(remoteName).AbsoluteUri);
        move.Headers.Add("Overwrite", "T");
        using var moved = await _http.SendAsync(move, cancellationToken);
        await EnsureSuccessAsync(moved, "MOVE");
    }

    public async Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(FileUri(remoteName), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, "GET");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamCopy.BufferSize, useAsync: true);
        await StreamCopy.CopyAsync(input, output, progress, cancellationToken);
    }

    public async Task DeleteAsync(string remoteName, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync(FileUri(remoteName), cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, "DELETE");
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private Uri FileUri(string name) => new(_folder, Uri.EscapeDataString(name));

    /// <summary>Creates the target folder and its parents (MKCOL) when missing.</summary>
    private async Task EnsureFolderAsync(CancellationToken cancellationToken)
    {
        var segments = _folder.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = new UriBuilder(_folder) { Path = "/" }.Uri;
        foreach (var segment in segments)
        {
            current = new Uri(current, segment + "/");
            using var mkcol = await _http.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), current), cancellationToken);

            // 405 = already exists; 401/403 on parent folders we cannot see is fine as long as the last one works.
            if (!mkcol.IsSuccessStatusCode && mkcol.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.Unauthorized))
            {
                await EnsureSuccessAsync(mkcol, "MKCOL");
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"WebDAV {operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}. {(body.Length > 300 ? body[..300] : body)}", null, response.StatusCode);
        }
    }
}
