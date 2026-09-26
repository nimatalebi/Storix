using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Destinations;

public sealed class TelegramPart
{
    public long MessageId { get; set; }

    public string FileId { get; set; } = string.Empty;

    public long Size { get; set; }
}

public sealed class TelegramCatalogEntry
{
    public long Size { get; set; }

    public DateTimeOffset Uploaded { get; set; }

    public List<TelegramPart> Parts { get; set; } = [];
}

/// <summary>
/// The list of files stored in a chat. Bots cannot read chat history, so this list is itself sent to the chat as a
/// small document and pinned; any Storix installation with the bot token finds it again through getChat.
/// </summary>
public sealed class TelegramCatalog
{
    public const string FormatName = "storix-telegram-catalog";
    public const string FileName = "storix-catalog.json";

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = 1;

    public Dictionary<string, TelegramCatalogEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Stores backups as documents in a Telegram (or Bale) channel or group: an archive-only copy for disasters.
/// Files larger than the part size (47 MB, under the 50 MB upload limit) are sent as name.001, name.002...
/// and every file is listed in a pinned catalog, which retention uses. Storix does not restore from Telegram:
/// the files are downloaded in the Telegram app and restored from disk (parts are joined automatically).
/// A relay (Cloudflare Worker, local Bot API server) can be used when the server cannot reach the API directly.
/// </summary>
public sealed class TelegramDestination : IBackupDestination
{
    // One catalog per chat, shared by every job that uses the chat: changes are serialized per process.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private readonly TelegramOptions _options;
    private readonly int _maxUploadKBps;
    private readonly TelegramBotApi _api;
    private readonly string _chatId;
    private readonly string _cachePath;

    public TelegramDestination(TelegramOptions options, int maxUploadKBps = 0, HttpClient? http = null, string? cacheFolder = null)
    {
        _options = options;
        _maxUploadKBps = maxUploadKBps;
        if (string.IsNullOrWhiteSpace(options.BotToken) || string.IsNullOrWhiteSpace(options.ChatId))
        {
            throw new InvalidOperationException("Telegram: the bot token and the chat id are required.");
        }

        _chatId = options.ChatId.Trim();
        var baseUrl = string.IsNullOrWhiteSpace(options.ApiBaseUrl) ? TelegramBotApi.DefaultBaseUrl(options.Service) : options.ApiBaseUrl;
        _api = new TelegramBotApi(options.BotToken, baseUrl, options.RelayKey, http);

        // Local copy of where the catalog is, used when the pinned message cannot be read (e.g. someone pinned another message).
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(options.BotToken.Trim() + "|" + _chatId)))[..16];
        _cachePath = Path.Combine(cacheFolder ?? Path.Combine(StorixPaths.DataDirectory, "telegram"), key + ".json");
    }

    private long PartSize => Math.Clamp(_options.PartSizeMb, 1, 2000) * 1024L * 1024L;

    private SemaphoreSlim Lock => Locks.GetOrAdd(_api.BaseUrl + "|" + _chatId, _ => new SemaphoreSlim(1, 1));

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        await _api.CallAsync("getMe", new Dictionary<string, string>(), cancellationToken);
        try
        {
            await _api.CallAsync("getChat", new Dictionary<string, string> { ["chat_id"] = _chatId }, cancellationToken);
        }
        catch (TelegramApiException ex) when (ex.Code is 400 or 403)
        {
            throw new InvalidOperationException($"The bot cannot access chat {_chatId}. Add the bot to the channel or group as an administrator. ({ex.Message})");
        }

        await LoadCatalogAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(CancellationToken cancellationToken)
    {
        var (catalog, _) = await LoadCatalogAsync(cancellationToken);
        return catalog.Files.Select(f => new RemoteFile(f.Key, f.Value.Size, f.Value.Uploaded)).ToList();
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var entry = new TelegramCatalogEntry { Uploaded = DateTimeOffset.UtcNow };
        await using (var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamCopy.BufferSize, useAsync: true))
        {
            entry.Size = file.Length;
            var count = Math.Max(1, (int)((file.Length + PartSize - 1) / PartSize));
            var buffer = new byte[Math.Min(PartSize, Math.Max(file.Length, 1))];
            try
            {
                for (var index = 0; index < count; index++)
                {
                    var length = (int)Math.Min(PartSize, file.Length - file.Position);
                    await file.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);
                    var name = count == 1 ? remoteName : $"{remoteName}.{index + 1:D3}";
                    entry.Parts.Add(await SendPartAsync(buffer, length, name, $"{remoteName} ({index + 1}/{count})", cancellationToken));
                    progress?.Report(file.Position);
                }
            }
            catch
            {
                // Parts already sent are not in the catalog: remove them so they do not pile up in the chat.
                await DeleteMessagesAsync(entry.Parts, CancellationToken.None);
                throw;
            }
        }

        // The file only becomes visible once it is in the catalog (like a rename after upload).
        List<TelegramPart>? replaced = null;
        await UpdateCatalogAsync(catalog =>
        {
            replaced = catalog.Files.TryGetValue(remoteName, out var old) ? old.Parts : null;
            catalog.Files[remoteName] = entry;
        }, cancellationToken);

        if (replaced is not null)
        {
            await DeleteMessagesAsync(replaced, CancellationToken.None);
        }
    }

    /// <summary>
    /// Telegram is an archive-only destination: Storix does not restore from it (bots can only download files up
    /// to 20 MB). Download the files in the Telegram app and restore them from disk.
    /// </summary>
    public Task DownloadAsync(string remoteName, string localPath, IProgress<long>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "Telegram is an archive-only destination. Download the backup files (all parts .001, .002... and the .sha256) from the chat " +
            "in the Telegram app, then use Restore → From a backup file (or 'storix restore <file>').");

    public async Task DeleteAsync(string remoteName, CancellationToken cancellationToken)
    {
        List<TelegramPart>? removed = null;
        await UpdateCatalogAsync(catalog =>
        {
            if (catalog.Files.Remove(remoteName, out var entry))
            {
                removed = entry.Parts;
            }
        }, cancellationToken);

        if (removed is not null)
        {
            await DeleteMessagesAsync(removed, cancellationToken);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<TelegramPart> SendPartAsync(byte[] buffer, int length, string fileName, string caption, CancellationToken cancellationToken)
    {
        await using var content = ThrottledStream.Wrap(new MemoryStream(buffer, 0, length, writable: false), _maxUploadKBps);
        var result = await _api.CallAsync(
            "sendDocument",
            new Dictionary<string, string> { ["chat_id"] = _chatId, ["caption"] = caption, ["disable_notification"] = "true", ["disable_content_type_detection"] = "true" },
            ("document", fileName, content),
            TimeSpan.FromMinutes(30),
            cancellationToken);

        var document = result.GetProperty("document");
        var part = new TelegramPart
        {
            MessageId = result.GetProperty("message_id").GetInt64(),
            FileId = document.GetProperty("file_id").GetString()!,
            Size = document.TryGetProperty("file_size", out var size) ? size.GetInt64() : length,
        };

        return part.Size == length ? part : throw new IOException($"Telegram stored {part.Size} bytes of '{fileName}' instead of {length}.");
    }

    private async Task DeleteMessagesAsync(IEnumerable<TelegramPart> parts, CancellationToken cancellationToken)
    {
        foreach (var part in parts)
        {
            try
            {
                await _api.CallAsync("deleteMessage", new Dictionary<string, string> { ["chat_id"] = _chatId, ["message_id"] = part.MessageId.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
            }
            catch (TelegramApiException)
            {
                // The bot may lack the delete right (or the message is gone): the file is already out of the catalog.
            }
        }
    }

    // ------------------------------------------------------------------ catalog

    private sealed record CatalogLocation(long MessageId, string FileId);

    private async Task UpdateCatalogAsync(Action<TelegramCatalog> change, CancellationToken cancellationToken)
    {
        await Lock.WaitAsync(cancellationToken);
        try
        {
            var (catalog, previous) = await LoadCatalogAsync(cancellationToken);
            change(catalog);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog, StorixJson.Options);
            var sent = await _api.CallAsync(
                "sendDocument",
                new Dictionary<string, string> { ["chat_id"] = _chatId, ["caption"] = "Storix catalog (keep this message pinned)", ["disable_notification"] = "true" },
                ("document", TelegramCatalog.FileName, new MemoryStream(bytes)),
                TimeSpan.FromMinutes(2),
                cancellationToken);
            var location = new CatalogLocation(sent.GetProperty("message_id").GetInt64(), sent.GetProperty("document").GetProperty("file_id").GetString()!);

            // Save the location locally first: even if pinning is not allowed, this machine keeps finding it.
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            await File.WriteAllTextAsync(_cachePath, JsonSerializer.Serialize(location), cancellationToken);
            try
            {
                await _api.CallAsync("pinChatMessage", new Dictionary<string, string>
                {
                    ["chat_id"] = _chatId,
                    ["message_id"] = location.MessageId.ToString(CultureInfo.InvariantCulture),
                    ["disable_notification"] = "true",
                }, cancellationToken);
            }
            catch (TelegramApiException)
            {
                // Without the pin right the catalog is only found from this machine (see the local copy above).
            }

            if (previous is not null)
            {
                await DeleteMessagesAsync([new TelegramPart { MessageId = previous.MessageId }], CancellationToken.None);
            }
        }
        finally
        {
            Lock.Release();
        }
    }

    private async Task<(TelegramCatalog Catalog, CatalogLocation? Location)> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        foreach (var location in await FindCatalogAsync(cancellationToken))
        {
            try
            {
                using var buffer = new MemoryStream();
                await _api.DownloadAsync(location.FileId, buffer, TimeSpan.FromMinutes(2), cancellationToken);
                var catalog = JsonSerializer.Deserialize<TelegramCatalog>(buffer.ToArray(), StorixJson.Options);
                if (catalog is { Format: TelegramCatalog.FormatName })
                {
                    catalog.Files = new Dictionary<string, TelegramCatalogEntry>(catalog.Files, StringComparer.OrdinalIgnoreCase);
                    return (catalog, location);
                }
            }
            catch (TelegramApiException)
            {
                // Deleted or unreadable: try the next place.
            }
        }

        return (new TelegramCatalog(), null);
    }

    /// <summary>Where the catalog may be: the pinned message of the chat, then the location saved on this machine.</summary>
    private async Task<List<CatalogLocation>> FindCatalogAsync(CancellationToken cancellationToken)
    {
        var result = new List<CatalogLocation>();
        var chat = await _api.CallAsync("getChat", new Dictionary<string, string> { ["chat_id"] = _chatId }, cancellationToken);
        if (chat.TryGetProperty("pinned_message", out var pinned)
            && pinned.TryGetProperty("document", out var document)
            && document.TryGetProperty("file_name", out var name) && name.GetString() == TelegramCatalog.FileName)
        {
            result.Add(new CatalogLocation(pinned.GetProperty("message_id").GetInt64(), document.GetProperty("file_id").GetString()!));
        }

        if (File.Exists(_cachePath))
        {
            try
            {
                if (JsonSerializer.Deserialize<CatalogLocation>(await File.ReadAllTextAsync(_cachePath, cancellationToken)) is { } cached
                    && result.All(r => r.MessageId != cached.MessageId))
                {
                    result.Add(cached);
                }
            }
            catch (JsonException)
            {
            }
        }

        // Message ids grow over time: the newest catalog wins (e.g. when the last pin failed).
        return [.. result.OrderByDescending(r => r.MessageId)];
    }
}
