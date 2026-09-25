using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Tests;

/// <summary>Contract tests of the Dropbox and OneDrive destinations against in-memory fakes of their HTTP APIs.</summary>
public class CloudApiTests
{
    internal static async Task RunContractAsync(IBackupDestination destination, TempDirectory temp, int largeSize)
    {
        var small = temp.Combine("small.bin");
        var large = temp.Combine("large.bin");
        await File.WriteAllBytesAsync(small, RandomNumberGenerator.GetBytes(1000));
        await File.WriteAllBytesAsync(large, RandomNumberGenerator.GetBytes(largeSize));

        await destination.TestAsync(CancellationToken.None);
        Assert.Empty(await destination.ListAsync(CancellationToken.None));

        await destination.UploadAsync(small, "a_20260101_000000.zip", null, CancellationToken.None);
        await destination.UploadAsync(large, "a_20260102_000000.zip", null, CancellationToken.None);

        var files = (await destination.ListAsync(CancellationToken.None)).OrderBy(f => f.Name).ToList();
        Assert.Equal(["a_20260101_000000.zip", "a_20260102_000000.zip"], files.Select(f => f.Name));
        Assert.Equal([1000L, largeSize], files.Select(f => f.Size));

        await destination.DownloadAsync("a_20260102_000000.zip", temp.Combine("back.bin"), null, CancellationToken.None);
        Assert.Equal(await File.ReadAllBytesAsync(large), await File.ReadAllBytesAsync(temp.Combine("back.bin")));

        await destination.DeleteAsync("a_20260101_000000.zip", CancellationToken.None);
        await destination.DeleteAsync("missing.zip", CancellationToken.None);
        Assert.Single(await destination.ListAsync(CancellationToken.None));
    }

    private sealed class FakeDropbox : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MemoryStream> _sessions = [];
        public int Refreshes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/oauth2/token")
            {
                Refreshes++;
                return Json(new { access_token = "at", expires_in = 14400 });
            }

            Assert.Equal("Bearer at", request.Headers.Authorization?.ToString());
            var arg = request.Headers.TryGetValues("Dropbox-API-Arg", out var values) ? JsonDocument.Parse(values.Single()).RootElement : default;
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            JsonElement Json_() => JsonDocument.Parse(body).RootElement;

            switch (path)
            {
                case "/2/files/create_folder_v2":
                    return Json(new { metadata = new { } });
                case "/2/files/list_folder":
                    var folder = Json_().GetProperty("path").GetString()!;
                    return Json(new
                    {
                        entries = _files.Where(f => f.Key.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
                            .Select(f => new Dictionary<string, object> { [".tag"] = "file", ["name"] = f.Key.Split('/')[^1], ["size"] = f.Value.Length, ["server_modified"] = "2026-01-01T00:00:00Z" }),
                        has_more = false,
                        cursor = "c",
                    });
                case "/2/files/upload_session/start":
                    var id = Guid.NewGuid().ToString("N");
                    _sessions[id] = new MemoryStream();
                    _sessions[id].Write(body);
                    return Json(new { session_id = id });
                case "/2/files/upload_session/append_v2":
                    var append = _sessions[arg.GetProperty("cursor").GetProperty("session_id").GetString()!];
                    Assert.Equal(append.Length, arg.GetProperty("cursor").GetProperty("offset").GetInt64());
                    append.Write(body);
                    return Json(new { });
                case "/2/files/upload_session/finish":
                    var finished = _sessions[arg.GetProperty("cursor").GetProperty("session_id").GetString()!];
                    finished.Write(body);
                    _files[arg.GetProperty("commit").GetProperty("path").GetString()!] = finished.ToArray();
                    return Json(new { });
                case "/2/files/download":
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_files[arg.GetProperty("path").GetString()!]) };
                case "/2/files/delete_v2":
                    return _files.Remove(Json_().GetProperty("path").GetString()!)
                        ? Json(new { })
                        : new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("""{"error_summary":"path_lookup/not_found/"}""") };
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }

    private sealed class FakeOneDrive : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Name, MemoryStream Data)> _sessions = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "login.microsoftonline.com")
            {
                return Json(new { access_token = "at", refresh_token = "rotated", expires_in = 3600 });
            }

            if (uri.Host == "upload.example")
            {
                Assert.Null(request.Headers.Authorization);
                var (name, data) = _sessions[uri.AbsolutePath];
                var range = request.Content!.Headers.ContentRange!;
                Assert.Equal(data.Length, range.From);
                data.Write(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                if (data.Length == range.Length)
                {
                    _files[name] = data.ToArray();
                    return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
                }

                return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{}") };
            }

            Assert.Equal("Bearer at", request.Headers.Authorization?.ToString());
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            const string root = "/v1.0/me/drive/root:/";
            if (path == "/v1.0/me/drive")
            {
                return Json(new { id = "drive" });
            }

            if (path.EndsWith(":/children", StringComparison.Ordinal))
            {
                var folder = path[root.Length..^":/children".Length];
                var items = _files.Where(f => f.Key.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                return items.Count == 0 && !_sessions.Values.Any()
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json(new { value = items.Select(f => new { name = f.Key.Split('/')[^1], size = f.Value.Length, lastModifiedDateTime = "2026-01-01T00:00:00Z", file = new { } }) });
            }

            if (path.EndsWith(":/createUploadSession", StringComparison.Ordinal))
            {
                var id = "/session/" + Guid.NewGuid().ToString("N");
                _sessions[id] = (path[root.Length..^":/createUploadSession".Length], new MemoryStream());
                return Json(new Dictionary<string, string> { ["uploadUrl"] = "https://upload.example" + id });
            }

            if (path.EndsWith(":/content", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_files[path[root.Length..^":/content".Length]]) };
            }

            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(_files.Remove(path[root.Length..]) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Dropbox_contract_with_multi_chunk_upload()
    {
        using var temp = new TempDirectory();
        var fake = new FakeDropbox();
        var destination = new DropboxDestination(new DropboxOptions { AppKey = "key", RefreshToken = "rt", Folder = "/Backups/server1" }, 0, new HttpClient(fake));

        await RunContractAsync(destination, temp, (9 * 1024 * 1024) + 123);
        Assert.Equal(1, fake.Refreshes);
    }

    [Fact]
    public async Task OneDrive_contract_with_resumable_upload_and_token_rotation()
    {
        using var temp = new TempDirectory();
        var id = Guid.NewGuid();
        var destination = new OneDriveDestination(new OneDriveOptions { ClientId = "client", RefreshToken = "original", Folder = "Backups/server 1" }, id, 0, new HttpClient(new FakeOneDrive()));

        await RunContractAsync(destination, temp, (21 * 1024 * 1024) + 77);
        Assert.Equal("rotated", Security.OAuthTokenStore.Load($"onedrive:{id}"));
    }

    [Fact]
    public async Task Not_signed_in_is_reported_clearly()
    {
        var dropbox = new DropboxDestination(new DropboxOptions { AppKey = "k" }, 0, new HttpClient(new FakeDropbox()));
        Assert.Contains("not connected", (await Assert.ThrowsAsync<InvalidOperationException>(() => dropbox.ListAsync(CancellationToken.None))).Message);
    }

    [Fact]
    public void Unc_share_root_is_extracted()
    {
        Assert.Equal(@"\\nas\backups", NetworkShare.ShareRoot(@"\\nas\backups\server1\daily"));
        Assert.Throws<ArgumentException>(() => NetworkShare.ShareRoot(@"\\nas"));
        using var none = NetworkShare.Connect(@"C:\local", "user", "pw"); // Not UNC: no connection.
    }
}
