using System.ComponentModel;

namespace NT.Storix.Core.Models;

public enum NotificationChannelKind
{
    /// <summary>HTTP POST with a JSON document (optionally signed with HMAC-SHA256).</summary>
    Webhook,
    Telegram,
    /// <summary>Bale messenger bot (Telegram-compatible API).</summary>
    Bale,
    Slack,
    Teams,
    Discord,
}

public sealed class NotificationChannel
{
    [Browsable(false)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Category("General")]
    public string Name { get; set; } = "Channel";

    [Category("General")]
    public NotificationChannelKind Kind { get; set; } = NotificationChannelKind.Webhook;

    [Category("General")]
    public bool Enabled { get; set; } = true;

    [Category("General"), Description("Only send failures and alerts, not successful runs.")]
    public bool OnlyFailures { get; set; }

    [Category("Webhook / Slack / Teams / Discord"), Description("Webhook URL."), Secret]
    public string? Url { get; set; }

    [Category("Webhook / Slack / Teams / Discord"), Description("Webhook only: secret used to sign the body (header X-Storix-Signature: sha256=<hex>)."), PasswordPropertyText(true), Secret]
    public string? SigningSecret { get; set; }

    [Category("Telegram / Bale"), Description("Bot token from @BotFather (Telegram) or the Bale bot father."), PasswordPropertyText(true), Secret]
    public string? BotToken { get; set; }

    [Category("Telegram / Bale"), Description("Chat, group or channel id (e.g. 123456789 or @mychannel).")]
    public string? ChatId { get; set; }

    [Category("Telegram / Bale"), Description("Optional relay when this server cannot reach the Bot API, e.g. a Cloudflare Worker URL. Empty = the official API.")]
    public string? ApiBaseUrl { get; set; }

    [Category("Telegram / Bale"), Description("Shared key for the relay (X-Storix-Relay-Key header)."), PasswordPropertyText(true), Secret]
    public string? RelayKey { get; set; }

    public override string ToString() => $"{Name} ({Kind})";
}
