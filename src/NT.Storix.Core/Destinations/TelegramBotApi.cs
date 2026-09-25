using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Destinations;

/// <summary>Error returned by the Bot API ("ok": false).</summary>
public sealed class TelegramApiException(string method, int code, string description) : IOException($"Telegram {method} failed ({code}): {description}")
{
    public int Code { get; } = code;
}

/// <summary>
/// Minimal Telegram Bot API client (also used for Bale). All calls can go through a relay, e.g. a Cloudflare
/// Worker, by setting <see cref="BaseUrl"/>; the relay key is sent in <c>X-Storix-Relay-Key</c>. The bot token is
/// part of the URL, so exceptions and logs never include URLs.
/// </summary>
public sealed class TelegramBotApi
{
    public const string RelayHeader = "X-Storix-Relay-Key";
    public const string TelegramUrl = "https://api.telegram.org";
    public const string BaleUrl = "https://tapi.bale.ai";

    // Uploads of large parts over slow links: no client-wide timeout, each call has its own.
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "User-Agent", $"Storix/{StorixInfo.Version}" } },
    };

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string? _relayKey;

    public TelegramBotApi(string token, string? baseUrl, string? relayKey, HttpClient? http = null)
    {
        _token = token.Trim();
        BaseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? TelegramUrl : baseUrl.Trim()).TrimEnd('/');
        _relayKey = string.IsNullOrEmpty(relayKey) ? null : relayKey;
        _http = http ?? DefaultHttp;
    }

    public string BaseUrl { get; }

    /// <summary>How long to wait when the API says "Too Many Requests" before giving up.</summary>
    public static TimeSpan MaxRateLimitWait { get; set; } = TimeSpan.FromMinutes(2);

    public static string DefaultBaseUrl(TelegramService service) => service == TelegramService.Bale ? BaleUrl : TelegramUrl;

    /// <summary>Calls a method with form fields (and optionally one file) and returns the "result" element.</summary>
    public async Task<JsonElement> CallAsync(string method, IReadOnlyDictionary<string, string> fields, (string Field, string FileName, Stream Content)? file, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/bot{_token}/{method}");
            AddRelayKey(request);
            if (file is { } upload)
            {
                var form = new MultipartFormDataContent();
                foreach (var (name, value) in fields)
                {
                    form.Add(new StringContent(value), name);
                }

                // The caller owns the stream: disposing the request must not close it (a 429 retry sends it again).
                var content = new StreamContent(new KeepOpenStream(upload.Content));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(content, upload.Field, upload.FileName);
                request.Content = form;
            }
            else
            {
                request.Content = new FormUrlEncodedContent(fields);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Telegram {method} timed out.");
            }
            catch (HttpRequestException ex)
            {
                // The message of an HttpRequestException can contain the URL, and with it the token.
                throw new IOException($"Could not reach the Bot API ({BaseUrl}): {ex.HttpRequestError}.");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden && _relayKey is not null && response.Content.Headers.ContentType?.MediaType != "application/json")
                {
                    throw new UnauthorizedAccessException("The relay refused the request: check the relay key.");
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                }
                catch (JsonException)
                {
                    throw new IOException($"Telegram {method}: unexpected response {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                    {
                        return root.GetProperty("result").Clone();
                    }

                    var code = root.TryGetProperty("error_code", out var c) ? c.GetInt32() : (int)response.StatusCode;
                    var description = root.TryGetProperty("description", out var d) ? d.GetString() ?? "error" : "error";
                    if (code == 429 && root.TryGetProperty("parameters", out var parameters) && parameters.TryGetProperty("retry_after", out var retry))
                    {
                        var delay = TimeSpan.FromSeconds(Math.Max(1, retry.GetInt32()));
                        if (waited + delay <= MaxRateLimitWait)
                        {
                            waited += delay;
                            file?.Content.Seek(0, SeekOrigin.Begin);
                            await Task.Delay(delay, cancellationToken);
                            continue;
                        }
                    }

                    throw new TelegramApiException(method, code, description);
                }
            }
        }
    }

    public Task<JsonElement> CallAsync(string method, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken) =>
        CallAsync(method, fields, null, TimeSpan.FromSeconds(60), cancellationToken);

    /// <summary>Downloads a file by its file_id (getFile, then the file URL).</summary>
    public async Task DownloadAsync(string fileId, Stream destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = await CallAsync("getFile", new Dictionary<string, string> { ["file_id"] = fileId }, cancellationToken);
        var path = info.GetProperty("file_path").GetString()!;

        // A local Bot API server (--local) returns an absolute path on its own disk.
        if (Path.IsPathRooted(path) && File.Exists(path))
        {
            await using var local = File.OpenRead(path);
            await local.CopyToAsync(destination, cancellationToken);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/file/bot{_token}/{path}");
        AddRelayKey(request);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new IOException($"Downloading a file from the Bot API failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
            await body.CopyToAsync(destination, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Downloading a file from the Bot API timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"Could not reach the Bot API ({BaseUrl}): {ex.HttpRequestError}.");
        }
    }

    private void AddRelayKey(HttpRequestMessage request)
    {
        if (_relayKey is not null)
        {
            request.Headers.Add(RelayHeader, _relayKey);
        }
    }

    /// <summary>Passes everything through except Dispose.</summary>
    private sealed class KeepOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
