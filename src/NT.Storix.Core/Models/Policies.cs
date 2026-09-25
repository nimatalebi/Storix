namespace NT.Storix.Core.Models;

public sealed class RetentionPolicy
{
    /// <summary>Number of most recent backups to keep on each destination. 0 means unlimited.</summary>
    public int KeepLast { get; set; } = 7;

    /// <summary>Delete backups older than this many days. 0 means unlimited.</summary>
    public int KeepDays { get; set; }
}

public sealed class RetryPolicy
{
    public int MaxAttempts { get; set; } = 3;

    public int InitialDelaySeconds { get; set; } = 30;

    public double BackoffMultiplier { get; set; } = 2;
}

public sealed class NotificationOptions
{
    public bool OnSuccess { get; set; }

    public bool OnFailure { get; set; } = true;

    /// <summary>Comma or semicolon separated list of e-mail recipients.</summary>
    public string? EmailTo { get; set; }

    /// <summary>
    /// Dead man's switch: alert when the job has had no successful backup for this many hours. 0 disables it.
    /// </summary>
    public int AlertIfNoSuccessForHours { get; set; }

    /// <summary>
    /// Optional health-check URL pinged after every run (healthchecks.io, Uptime Kuma push monitors...).
    /// Supports the placeholders {status} (up/down) and {message}; without placeholders healthchecks.io
    /// conventions are used (URL on success, URL/fail on failure, URL/start when the run starts).
    /// </summary>
    [Secret]
    public string? HealthCheckUrl { get; set; }
}
