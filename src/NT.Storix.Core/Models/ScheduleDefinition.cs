namespace NT.Storix.Core.Models;

public enum ScheduleKind
{
    /// <summary>Only runs when started manually.</summary>
    Manual,
    Daily,
    Weekly,
    /// <summary>Standard 5-field cron expression (minute hour day month weekday).</summary>
    Cron,
}

public sealed class ScheduleDefinition
{
    public ScheduleKind Kind { get; set; } = ScheduleKind.Daily;

    /// <summary>Time of day for <see cref="ScheduleKind.Daily"/> and <see cref="ScheduleKind.Weekly"/>.</summary>
    public TimeSpan TimeOfDay { get; set; } = new(2, 0, 0);

    /// <summary>Days used by <see cref="ScheduleKind.Weekly"/>.</summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = [DayOfWeek.Friday];

    public string? CronExpression { get; set; }

    /// <summary>Windows or IANA time zone id. Empty means the machine's local time zone.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// When the service starts and a scheduled run was missed (machine off, service stopped), run the job once.
    /// </summary>
    public bool CatchUpMissedRuns { get; set; } = true;
}
