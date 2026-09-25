using Cronos;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Scheduling;

/// <summary>Turns a <see cref="ScheduleDefinition"/> into concrete occurrences.</summary>
public static class ScheduleCalculator
{
    /// <summary>Returns the cron expression equivalent to the schedule, or <c>null</c> for manual jobs.</summary>
    public static string? ToCron(ScheduleDefinition schedule)
    {
        var minute = schedule.TimeOfDay.Minutes;
        var hour = schedule.TimeOfDay.Hours;

        return schedule.Kind switch
        {
            ScheduleKind.Manual => null,
            ScheduleKind.Daily => $"{minute} {hour} * * *",
            ScheduleKind.Weekly => $"{minute} {hour} * * {FormatDays(schedule.DaysOfWeek)}",
            ScheduleKind.Cron => string.IsNullOrWhiteSpace(schedule.CronExpression) ? null : schedule.CronExpression.Trim(),
            _ => throw new NotSupportedException($"Schedule kind {schedule.Kind} is not supported."),
        };
    }

    /// <summary>Validates the schedule and returns an error message, or <c>null</c> if valid.</summary>
    public static string? Validate(ScheduleDefinition schedule)
    {
        if (schedule.Kind == ScheduleKind.Weekly && schedule.DaysOfWeek.Count == 0)
        {
            return "Select at least one day of the week.";
        }

        if (schedule.Kind == ScheduleKind.Cron && string.IsNullOrWhiteSpace(schedule.CronExpression))
        {
            return "Enter a cron expression.";
        }

        try
        {
            var cron = ToCron(schedule);
            if (cron is not null)
            {
                Parse(cron);
            }

            ResolveTimeZone(schedule.TimeZoneId);
            return null;
        }
        catch (Exception ex) when (ex is CronFormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return ex.Message;
        }
    }

    /// <summary>Returns the first occurrence strictly after <paramref name="fromUtc"/>, or <c>null</c> for manual jobs.</summary>
    public static DateTimeOffset? GetNextOccurrence(ScheduleDefinition schedule, DateTimeOffset fromUtc)
    {
        var cron = ToCron(schedule);
        if (cron is null)
        {
            return null;
        }

        var zone = ResolveTimeZone(schedule.TimeZoneId);
        return Parse(cron).GetNextOccurrence(fromUtc, zone, inclusive: false);
    }

    public static string Describe(ScheduleDefinition schedule) => schedule.Kind switch
    {
        ScheduleKind.Manual => "Manual",
        ScheduleKind.Daily => $"Daily at {schedule.TimeOfDay:hh\\:mm}",
        ScheduleKind.Weekly => $"Weekly on {string.Join(", ", schedule.DaysOfWeek.Order().Select(d => d.ToString()[..3]))} at {schedule.TimeOfDay:hh\\:mm}",
        ScheduleKind.Cron => $"Cron: {schedule.CronExpression}",
        _ => schedule.Kind.ToString(),
    };

    private static CronExpression Parse(string cron)
    {
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return CronExpression.Parse(cron, fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
    }

    private static TimeZoneInfo ResolveTimeZone(string? id) =>
        string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id);

    private static string FormatDays(IEnumerable<DayOfWeek> days) =>
        string.Join(',', days.Distinct().Order().Select(d => (int)d));
}
