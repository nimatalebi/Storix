using NT.Storix.Core.Models;
using NT.Storix.Core.Scheduling;

namespace NT.Storix.Core.Tests;

public class ScheduleTests
{
    private static readonly DateTimeOffset Monday = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Daily_runs_at_configured_time()
    {
        var schedule = new ScheduleDefinition { Kind = ScheduleKind.Daily, TimeOfDay = new TimeSpan(2, 30, 0), TimeZoneId = "UTC" };
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 2, 30, 0, TimeSpan.Zero), ScheduleCalculator.GetNextOccurrence(schedule, Monday));
    }

    [Fact]
    public void Weekly_picks_next_selected_day()
    {
        var schedule = new ScheduleDefinition
        {
            Kind = ScheduleKind.Weekly,
            TimeOfDay = new TimeSpan(23, 0, 0),
            DaysOfWeek = [DayOfWeek.Wednesday, DayOfWeek.Saturday],
            TimeZoneId = "UTC",
        };

        Assert.Equal("0 23 * * 3,6", ScheduleCalculator.ToCron(schedule));
        Assert.Equal(new DateTimeOffset(2026, 1, 7, 23, 0, 0, TimeSpan.Zero), ScheduleCalculator.GetNextOccurrence(schedule, Monday));
    }

    [Fact]
    public void Cron_expression_is_supported()
    {
        var schedule = new ScheduleDefinition { Kind = ScheduleKind.Cron, CronExpression = "0 */6 * * *", TimeZoneId = "UTC" };
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero), ScheduleCalculator.GetNextOccurrence(schedule, Monday));
    }

    [Fact]
    public void Manual_has_no_occurrence_and_invalid_cron_is_reported()
    {
        Assert.Null(ScheduleCalculator.GetNextOccurrence(new ScheduleDefinition { Kind = ScheduleKind.Manual }, Monday));
        Assert.NotNull(ScheduleCalculator.Validate(new ScheduleDefinition { Kind = ScheduleKind.Cron, CronExpression = "not a cron" }));
        Assert.NotNull(ScheduleCalculator.Validate(new ScheduleDefinition { Kind = ScheduleKind.Weekly, DaysOfWeek = [] }));
    }
}

public class DaylightSavingTests
{
    // Europe/Berlin: clocks go forward 2026-03-29 02:00 -> 03:00 and back 2026-10-25 03:00 -> 02:00.
    private static ScheduleDefinition Daily(int hour, int minute) =>
        new() { Kind = ScheduleKind.Daily, TimeOfDay = new TimeSpan(hour, minute, 0), TimeZoneId = "Europe/Berlin" };

    private static List<DateTimeOffset> Occurrences(ScheduleDefinition schedule, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var result = new List<DateTimeOffset>();
        var cursor = fromUtc;
        while (ScheduleCalculator.GetNextOccurrence(schedule, cursor) is { } next && next <= toUtc)
        {
            result.Add(next);
            cursor = next;
        }

        return result;
    }

    [Fact]
    public void Skipped_local_time_still_runs_once_on_spring_forward_day()
    {
        var runs = Occurrences(Daily(2, 30), new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero));

        var run = Assert.Single(runs);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), run); // 03:00 CEST, right after the gap.
    }

    [Fact]
    public void Repeated_local_time_runs_only_once_on_fall_back_day()
    {
        var runs = Occurrences(Daily(2, 30), new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

        var run = Assert.Single(runs);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), run); // First 02:30 (CEST).
    }

    [Fact]
    public void Daily_schedule_keeps_local_time_across_offset_changes()
    {
        var runs = Occurrences(Daily(9, 0), new DateTimeOffset(2026, 3, 27, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(4, runs.Count);
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        Assert.All(runs, r => Assert.Equal(9, TimeZoneInfo.ConvertTime(r, berlin).Hour));
        Assert.Equal(8, runs[0].UtcDateTime.Hour); // CET
        Assert.Equal(7, runs[^1].UtcDateTime.Hour); // CEST
    }

    [Fact]
    public void Hourly_cron_has_no_duplicates_or_gaps_in_utc_across_fall_back()
    {
        var schedule = new ScheduleDefinition { Kind = ScheduleKind.Cron, CronExpression = "0 * * * *", TimeZoneId = "Europe/Berlin" };
        var runs = Occurrences(schedule, new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 4, 0, 0, TimeSpan.Zero));

        Assert.Equal(runs.Distinct().Count(), runs.Count);
        Assert.All(runs.Zip(runs.Skip(1)), pair => Assert.Equal(TimeSpan.FromHours(1), pair.Second - pair.First));
    }

    [Fact]
    public void Missed_run_detection()
    {
        var schedule = new ScheduleDefinition { Kind = ScheduleKind.Daily, TimeOfDay = new TimeSpan(2, 0, 0), TimeZoneId = "UTC" };
        var now = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.True(ScheduleCalculator.HasMissedRun(schedule, now.AddDays(-2), now));
        Assert.False(ScheduleCalculator.HasMissedRun(schedule, new DateTimeOffset(2026, 5, 10, 2, 0, 5, TimeSpan.Zero), now));
        Assert.False(ScheduleCalculator.HasMissedRun(schedule, null, now));

        schedule.CatchUpMissedRuns = false;
        Assert.False(ScheduleCalculator.HasMissedRun(schedule, now.AddDays(-2), now));
    }
}
