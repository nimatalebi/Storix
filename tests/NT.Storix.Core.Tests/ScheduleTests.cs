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
