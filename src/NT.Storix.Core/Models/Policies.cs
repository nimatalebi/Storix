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
}
