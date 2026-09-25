namespace NT.Storix.Core.Models;

/// <summary>
/// A backup job: one source, a processing pipeline and one or more destinations.
/// </summary>
public sealed class BackupJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New backup job";

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    public ScheduleDefinition Schedule { get; set; } = new();

    public SourceDefinition Source { get; set; } = new();

    public ProcessingOptions Processing { get; set; } = new();

    public List<DestinationDefinition> Destinations { get; set; } = [];

    public RetentionPolicy Retention { get; set; } = new();

    public RetryPolicy Retry { get; set; } = new();

    public NotificationOptions Notifications { get; set; } = new();

    public JobHooks Hooks { get; set; } = new();

    /// <summary>
    /// File-name friendly identifier used as the prefix of every archive produced by this job.
    /// </summary>
    public string FilePrefix => Slug.From(Name, fallback: Id.ToString("N")[..8]);
}
