using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

/// <summary>In-memory Bot API with the limits that matter: 20 MB downloads, pinned message, relay key, 429s.</summary>
internal sealed class FakeTelegram : HttpMessageHandler
{
    public const string Token = "123456:TEST-token";
    private readonly Dictionary<long, (string FileName, byte[] Data)> _messages = [];
    private readonly Dictionary<string, long> _fileIds = [];
    private long _nextMessage = 100;

    public string BaseUrl { get; init; } = TelegramBotApi.TelegramUrl;

    public string? RequiredRelayKey { get; init; }

    public bool CanPin { get; set; } = true;

    public int RateLimitNextCalls { get; set; }

    public long? Pinned { get; set; }

    public List<string> Calls { get; } = [];

    public IReadOnlyCollection<string> FileNames => _messages.Values.Select(m => m.FileName).ToList();

    public int Messages => _messages.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        if (!url.StartsWith(BaseUrl + "/", StringComparison.Ordinal))
        {
            throw new HttpRequestException("wrong host");
        }

        if (RequiredRelayKey is not null && (!request.Headers.TryGetValues(TelegramBotApi.RelayHeader, out var keys) || keys.Single() != RequiredRelayKey))
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Forbidden") };
        }

        var path = url[(BaseUrl.Length + 1)..];
        if (path.StartsWith($"file/bot{Token}/", StringComparison.Ordinal))
        {
            var fileId = path[$"file/bot{Token}/".Length..];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_messages[_fileIds[fileId]].Data) };
        }

        Assert.StartsWith($"bot{Token}/", path);
        var method = path[$"bot{Token}/".Length..];
        Calls.Add(method);
        if (RateLimitNextCalls > 0)
        {
            RateLimitNextCalls--;
            return Json(new JsonObject { ["ok"] = false, ["error_code"] = 429, ["description"] = "Too Many Requests", ["parameters"] = new JsonObject { ["retry_after"] = 1 } });
        }

        var fields = new Dictionary<string, string>();
        (string Name, byte[] Data)? file = null;
        if (request.Content is MultipartFormDataContent form)
        {
            foreach (var part in form)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (part.Headers.ContentDisposition.FileName is { } fileName)
                {
                    file = (fileName.Trim('"'), await part.ReadAsByteArrayAsync(cancellationToken));
                }
                else
                {
                    fields[name] = await part.ReadAsStringAsync(cancellationToken);
                }
            }
        }
        else if (request.Content is not null)
        {
            foreach (var pair in (await request.Content.ReadAsStringAsync(cancellationToken)).Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=');
                fields[WebUtility.UrlDecode(kv[0])] = WebUtility.UrlDecode(kv[1]);
            }
        }

        switch (method)
        {
            case "getMe":
                return Ok(new JsonObject { ["id"] = 123456, ["is_bot"] = true });
            case "getChat":
                var chat = new JsonObject { ["id"] = -100 };
                if (Pinned is { } pinned && _messages.TryGetValue(pinned, out var message))
                {
                    chat["pinned_message"] = MessageJson(pinned, message.FileName, message.Data.Length);
                }

                return Ok(chat);
            case "sendDocument":
                Assert.Equal("-100", fields["chat_id"]);
                if (file!.Value.Data.Length > 50 * 1024 * 1024)
                {
                    return Error(413, "Request Entity Too Large");
                }

                var id = _nextMessage++;
                _messages[id] = (file.Value.Name, file.Value.Data);
                _fileIds["f" + id] = id;
                return Ok(MessageJson(id, file.Value.Name, file.Value.Data.Length));
            case "pinChatMessage":
                if (!CanPin)
                {
                    return Error(400, "Bad Request: not enough rights to manage pinned messages in the chat");
                }

                Pinned = long.Parse(fields["message_id"]);
                return Ok(true);
            case "deleteMessage":
                return _messages.Remove(long.Parse(fields["message_id"])) ? Ok(true) : Error(400, "Bad Request: message to delete not found");
            case "getFile":
                var fileMessage = _fileIds.TryGetValue(fields["file_id"], out var m) && _messages.ContainsKey(m) ? m : -1;
                if (fileMessage < 0)
                {
                    return Error(400, "Bad Request: invalid file_id");
                }

                return _messages[fileMessage].Data.Length > 20 * 1024 * 1024
                    ? Error(400, "Bad Request: file is too big")
                    : Ok(new JsonObject { ["file_id"] = fields["file_id"], ["file_path"] = fields["file_id"] });
            default:
                return Error(404, "Not Found");
        }
    }

    private static JsonObject MessageJson(long id, string fileName, long size) => new()
    {
        ["message_id"] = id,
        ["document"] = new JsonObject { ["file_id"] = "f" + id, ["file_name"] = fileName, ["file_size"] = size },
    };

    private static HttpResponseMessage Ok(JsonNode result) => Json(new JsonObject { ["ok"] = true, ["result"] = result });

    private static HttpResponseMessage Error(int code, string description) =>
        Json(new JsonObject { ["ok"] = false, ["error_code"] = code, ["description"] = description }, (HttpStatusCode)code);

    private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}

public class TelegramTests
{
    private static TelegramOptions Options(string? baseUrl = null, string? relayKey = null, int partMb = 19) =>
        new() { BotToken = FakeTelegram.Token, ChatId = "-100", ApiBaseUrl = baseUrl, RelayKey = relayKey, PartSizeMb = partMb };

    [Fact]
    public async Task Large_files_are_sent_in_parts_listed_and_downloaded_intact()
    {
        using var temp = new TempDirectory();
        var fake = new FakeTelegram();
        var data = RandomNumberGenerator.GetBytes(45 * 1024 * 1024 + 123);
        File.WriteAllBytes(temp.Combine("backup.zip.aes"), data);
        await using var destination = new TelegramDestination(Options(), http: new HttpClient(fake), cacheFolder: temp.Combine("cache"));

        await destination.TestAsync(CancellationToken.None);
        await destination.UploadAsync(temp.Combine("backup.zip.aes"), "backup.zip.aes", null, CancellationToken.None);

        Assert.Contains("backup.zip.aes.001", fake.FileNames);
        Assert.Contains("backup.zip.aes.003", fake.FileNames);
        var listed = Assert.Single(await destination.ListAsync(CancellationToken.None));
        Assert.Equal(("backup.zip.aes", (long)data.Length), (listed.Name, listed.Size));

        await destination.DownloadAsync("backup.zip.aes", temp.Combine("down.bin"), null, CancellationToken.None);
        Assert.Equal(data, File.ReadAllBytes(temp.Combine("down.bin")));
    }

    [Fact]
    public async Task Another_machine_finds_the_backups_through_the_pinned_catalog()
    {
        using var temp = new TempDirectory();
        var fake = new FakeTelegram();
        temp.WriteFile("a.txt", "hello");
        await using (var first = new TelegramDestination(Options(), http: new HttpClient(fake), cacheFolder: temp.Combine("machine1")))
        {
            await first.UploadAsync(temp.Combine("a.txt"), "a.txt", null, CancellationToken.None);
            await first.UploadAsync(temp.Combine("a.txt"), "b.txt", null, CancellationToken.None);
            await first.DeleteAsync("a.txt", CancellationToken.None);
        }

        await using var second = new TelegramDestination(Options(), http: new HttpClient(fake), cacheFolder: temp.Combine("machine2"));
        Assert.Equal(["b.txt"], (await second.ListAsync(CancellationToken.None)).Select(f => f.Name));

        // Old catalogs and deleted files do not stay in the chat: one catalog + b.txt.
        Assert.Equal(2, fake.Messages);
    }

    [Fact]
    public async Task Without_the_pin_right_this_machine_still_finds_the_newest_catalog()
    {
        using var temp = new TempDirectory();
        var fake = new FakeTelegram();
        temp.WriteFile("a.txt", "a");
        await using var destination = new TelegramDestination(Options(), http: new HttpClient(fake), cacheFolder: temp.Combine("cache"));
        await destination.UploadAsync(temp.Combine("a.txt"), "one", null, CancellationToken.None);

        fake.CanPin = false;
        await destination.UploadAsync(temp.Combine("a.txt"), "two", null, CancellationToken.None);

        Assert.Equal(["one", "two"], (await destination.ListAsync(CancellationToken.None)).Select(f => f.Name).Order());
    }

    [Fact]
    public async Task Relay_base_url_and_key_are_used_and_rate_limits_are_waited_out()
    {
        using var temp = new TempDirectory();
        var fake = new FakeTelegram { BaseUrl = "https://relay.example.workers.dev", RequiredRelayKey = "k3y" };
        temp.WriteFile("a.txt", "via relay");
        await using var destination = new TelegramDestination(Options("https://relay.example.workers.dev/", "k3y"), http: new HttpClient(fake), cacheFolder: temp.Combine("c"));

        fake.RateLimitNextCalls = 1;
        await destination.UploadAsync(temp.Combine("a.txt"), "a.txt", null, CancellationToken.None);
        await destination.DownloadAsync("a.txt", temp.Combine("b.txt"), null, CancellationToken.None);
        Assert.Equal("via relay", File.ReadAllText(temp.Combine("b.txt")));

        await using var wrongKey = new TelegramDestination(Options("https://relay.example.workers.dev", "nope"), http: new HttpClient(fake), cacheFolder: temp.Combine("c2"));
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrongKey.ListAsync(CancellationToken.None));
        Assert.Contains("relay key", error.Message);
    }

    [Fact]
    public async Task Errors_never_contain_the_bot_token()
    {
        using var temp = new TempDirectory();
        var unreachable = new HttpClient(new FailingHandler());
        await using var destination = new TelegramDestination(Options(), http: unreachable, cacheFolder: temp.Combine("c"));

        var error = await Assert.ThrowsAsync<IOException>(() => destination.ListAsync(CancellationToken.None));

        Assert.DoesNotContain("TEST-token", error.ToString());
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, $"Connection refused ({request.RequestUri})");
    }

    [Fact]
    public async Task Full_backup_and_restore_through_telegram()
    {
        using var temp = new TempDirectory();
        var fake = new FakeTelegram();
        var database = new StorixDatabase(temp.Combine("s.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        temp.WriteFile("src/report.txt", "quarterly numbers");
        var job = new BackupJob
        {
            Name = "To Telegram",
            Source = { Files = { Paths = [temp.Combine("src")] } },
            Processing = { Encrypt = true, EncryptionPassword = "pw" },
            Destinations = [new DestinationDefinition { Name = "Telegram", Kind = DestinationKind.Telegram, Telegram = Options() }],
        };
        var factory = new TestFactory(fake, temp.Combine("cache"));
        var runner = new BackupJobRunner(new RunRepository(database), settings, new SourceFactory(), factory, Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);

        var run = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);

        Assert.True(run.Status == RunStatus.Succeeded, run.Log);
        var restore = new RestoreService(factory);
        await restore.RestoreFromDestinationAsync(job.Destinations[0], run.FileName!, new RestoreRequest(temp.Combine("r"), "pw"), null, CancellationToken.None);
        Assert.Equal("quarterly numbers", File.ReadAllText(temp.Combine("r", "src", "report.txt")));
    }

    [Fact]
    public void Telegram_notifications_can_use_a_relay()
    {
        var channel = new NotificationChannel { Kind = NotificationChannelKind.Telegram, BotToken = "1:x", ChatId = "5", ApiBaseUrl = "https://relay.example/", RelayKey = "k" };

        using var request = ChannelNotifier.BuildRequest(channel, new Notification(NotificationEvent.Test, "T", "Body"));

        Assert.Equal("https://relay.example/bot1:x/sendMessage", request.RequestUri!.ToString());
        Assert.Equal("k", request.Headers.GetValues(TelegramBotApi.RelayHeader).Single());
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    private sealed class TestFactory(FakeTelegram fake, string cache) : IDestinationFactory
    {
        public IBackupDestination Create(DestinationDefinition definition) =>
            new TelegramDestination(definition.Telegram, definition.MaxUploadKBps, new HttpClient(fake), cache);
    }
}
