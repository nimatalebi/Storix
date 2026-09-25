namespace NT.Storix.Core.Models;

public enum RunStatus
{
    Running,
    Succeeded,
    PartiallySucceeded,
    Failed,
    Cancelled,
    /// <summary>The process stopped while the run was in progress (crash, reboot, forced stop).</summary>
    Interrupted,
}

public enum RunTrigger
{
    Schedule,
    Manual,
    /// <summary>A test restore (restore drill), not a backup.</summary>
    RestoreDrill,
}

public sealed class BackupRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    public RunTrigger Trigger { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }

    public string? FileName { get; set; }

    public long? SizeBytes { get; set; }

    public string? Sha256 { get; set; }

    public string? Message { get; set; }

    public string? Log { get; set; }

    public TimeSpan? Duration => FinishedAt - StartedAt;
}
