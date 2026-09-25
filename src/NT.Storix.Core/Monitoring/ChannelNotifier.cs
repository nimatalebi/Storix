using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NT.Storix.Core.Models;
using NT.Storix.Core.Persistence;

namespace NT.Storix.Core.Monitoring;

/// <summary>Sends notifications to webhooks and chat apps (Telegram, Bale, Slack, Teams, Discord).</summary>
public sealed class ChannelNotifier(SettingsRepository settings, HttpClient http, ILogger<ChannelNotifier> logger) : INotifier
{
    public const string SignatureHeader = "X-Storix-Signature";

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public async Task NotifyAsync(Notification notification, CancellationToken cancellationToken)
    {
        foreach (var channel in settings.Get().Channels.Where(c => c.Enabled && (notification.IsProblem || notification.Event == NotificationEvent.Test || !c.OnlyFailures)))
        {
            try
            {
                await SendAsync(http, channel, notification, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never log the URL or token: they are secrets.
                logger.LogError("Notification channel '{Channel}' ({Kind}) failed: {Error}", channel.Name, channel.Kind, ex.Message);
            }
        }
    }

    public static async Task SendAsync(HttpClient http, NotificationChannel channel, Notification notification, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(channel, notification);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{channel.Kind} returned {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }
    }

    internal static HttpRequestMessage BuildRequest(NotificationChannel channel, Notification notification)
    {
        var plain = $"{notification.Title}\n\n{notification.Text}";
        switch (channel.Kind)
        {
            case NotificationChannelKind.Telegram:
            case NotificationChannelKind.Bale:
                if (string.IsNullOrWhiteSpace(channel.BotToken) || string.IsNullOrWhiteSpace(channel.ChatId))
                {
                    throw new InvalidOperationException("Bot token and chat id are required.");
                }

                var host = string.IsNullOrWhiteSpace(channel.ApiBaseUrl)
                    ? channel.Kind == NotificationChannelKind.Telegram ? Destinations.TelegramBotApi.TelegramUrl : Destinations.TelegramBotApi.BaleUrl
                    : channel.ApiBaseUrl.Trim().TrimEnd('/');
                var telegram = new HttpRequestMessage(HttpMethod.Post, $"{host}/bot{channel.BotToken.Trim()}/sendMessage")
                {
                    Content = JsonContent.Create(new
                    {
                        chat_id = channel.ChatId.Trim(),
                        text = $"<b>{WebUtility.HtmlEncode(notification.Title)}</b>\n\n{WebUtility.HtmlEncode(notification.Text)}",
                        parse_mode = "HTML",
                        disable_web_page_preview = true,
                    }),
                };
                if (!string.IsNullOrEmpty(channel.RelayKey))
                {
                    telegram.Headers.Add(Destinations.TelegramBotApi.RelayHeader, channel.RelayKey);
                }

                return telegram;

            case NotificationChannelKind.Slack:
            case NotificationChannelKind.Teams:
                return new HttpRequestMessage(HttpMethod.Post, RequireUrl(channel)) { Content = JsonContent.Create(new { text = plain }) };

            case NotificationChannelKind.Discord:
                return new HttpRequestMessage(HttpMethod.Post, RequireUrl(channel))
                {
                    Content = JsonContent.Create(new { content = plain.Length > 1900 ? plain[..1900] + "..." : plain }),
                };

            default:
                var body = JsonSerializer.Serialize(WebhookPayload(notification), PayloadOptions);
                var message = new HttpRequestMessage(HttpMethod.Post, RequireUrl(channel))
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(channel.SigningSecret))
                {
                    message.Headers.Add(SignatureHeader, "sha256=" + Sign(body, channel.SigningSecret));
                }

                return message;
        }
    }

    public static string Sign(string body, string secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    internal static object WebhookPayload(Notification n) => new
    {
        @event = n.Event.ToString().ToLowerInvariant(),
        title = n.Title,
        text = n.Text,
        machine = Environment.MachineName,
        version = StorixInfo.Version,
        timestamp = DateTimeOffset.UtcNow,
        job = n.Job is null ? null : new { id = n.Job.Id, name = n.Job.Name },
        run = n.Run is null ? null : new
        {
            id = n.Run.Id,
            status = n.Run.Status.ToString(),
            trigger = n.Run.Trigger.ToString(),
            startedAt = n.Run.StartedAt,
            finishedAt = n.Run.FinishedAt,
            durationSeconds = n.Run.Duration?.TotalSeconds,
            fileName = n.Run.FileName,
            sizeBytes = n.Run.SizeBytes,
            sha256 = n.Run.Sha256,
            message = n.Run.Message,
        },
    };

    private static string RequireUrl(NotificationChannel channel) =>
        string.IsNullOrWhiteSpace(channel.Url) ? throw new InvalidOperationException("URL is required.") : channel.Url.Trim();
}
