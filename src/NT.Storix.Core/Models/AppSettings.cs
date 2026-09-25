namespace NT.Storix.Core.Models;

/// <summary>Machine-wide settings shared by the service and the manager UI.</summary>
public sealed class AppSettings
{
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>Working folder for temporary archives. Empty means <c>%ProgramData%\Storix\staging</c>.</summary>
    public string? StagingDirectory { get; set; }

    /// <summary>Ask for the Windows password before restores, deletions, exports with secrets and service changes.</summary>
    public bool RequireWindowsConfirmation { get; set; }

    /// <summary>Hold uploads while Windows reports the internet connection as metered.</summary>
    public bool PauseOnMeteredConnection { get; set; }

    /// <summary>Run history older than this many days is removed. 0 keeps everything.</summary>
    public int HistoryRetentionDays { get; set; } = 365;

    public SmtpSettings Smtp { get; set; } = new();

    /// <summary>Chat and webhook channels that receive notifications of every job.</summary>
    public List<NotificationChannel> Channels { get; set; } = [];

    public WeeklySummarySettings WeeklySummary { get; set; } = new();

    public ObservabilitySettings Observability { get; set; } = new();

    /// <summary>Storix Manager looks for a newer release on GitHub once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Also offer pre-release (beta) versions.</summary>
    public bool IncludePrereleaseUpdates { get; set; }
}

public sealed class ObservabilitySettings
{
    /// <summary>Expose Prometheus metrics on http://localhost:{MetricsPort}/metrics.</summary>
    public bool MetricsEnabled { get; set; }

    public int MetricsPort { get; set; } = 9464;

    /// <summary>Also listen on all network interfaces (for a remote Prometheus server); needs a firewall rule.</summary>
    public bool MetricsRemoteAccess { get; set; }

    /// <summary>OpenTelemetry OTLP endpoint for traces, e.g. http://otel-collector:4317. Empty = off.</summary>
    public string? OtlpEndpoint { get; set; }
}

public sealed class WeeklySummarySettings
{
    public bool Enabled { get; set; }

    /// <summary>E-mail recipients (comma separated). Chat channels also receive the summary.</summary>
    public string? Recipients { get; set; }

    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;

    public int Hour { get; set; } = 8;
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
