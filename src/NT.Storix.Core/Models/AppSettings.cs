namespace NT.Storix.Core.Models;

/// <summary>Machine-wide settings shared by the service and the manager UI.</summary>
public sealed class AppSettings
{
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>Working folder for temporary archives. Empty means <c>%ProgramData%\Storix\staging</c>.</summary>
    public string? StagingDirectory { get; set; }

    /// <summary>Run history older than this many days is removed. 0 keeps everything.</summary>
    public int HistoryRetentionDays { get; set; } = 365;

    public SmtpSettings Smtp { get; set; } = new();

    /// <summary>Chat and webhook channels that receive notifications of every job.</summary>
    public List<NotificationChannel> Channels { get; set; } = [];
}

public sealed class SmtpSettings
{
    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool UseSsl { get; set; } = true;

    public string? UserName { get; set; }

    [Secret]
    public string? Password { get; set; }

    public string From { get; set; } = string.Empty;
}
