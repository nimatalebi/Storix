using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;

namespace NT.Storix.Core.Tests;

public class TimeTextTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero).ToLocalTime();

    [Theory]
    [InlineData(-20, "just now")]
    [InlineData(20, "in less than a minute")]
    [InlineData(-300, "5 min ago")]
    [InlineData(3 * 3600, "in 3 h")]
    [InlineData(-2 * 3600, "2 h ago")]
    public void Close_times_are_relative(int seconds, string expected)
    {
        Assert.Equal(expected, TimeText.Relative(Now.AddSeconds(seconds), Now));
    }

    [Fact]
    public void Far_times_name_the_day()
    {
        var today = new DateTimeOffset(Now.Date, Now.Offset);
        Assert.Equal("Tomorrow 03:00", TimeText.Relative(today.AddDays(1).AddHours(3), Now));
        Assert.Equal("Yesterday 21:15", TimeText.Relative(today.AddDays(-1).AddHours(21).AddMinutes(15), Now));
        Assert.StartsWith("2026-", TimeText.Relative(today.AddDays(30), Now));
    }

    [Fact]
    public void Persian_uses_persian_words_digits_and_calendar()
    {
        Assert.Equal("۵ دقیقه پیش", TimeText.Relative(Now.AddMinutes(-5), Now, persian: true));
        Assert.Equal("۳ ساعت دیگر", TimeText.Relative(Now.AddHours(3).AddMinutes(1), Now, persian: true));
        Assert.Equal("1404/12/19", TimeText.SolarHijri(new DateTime(2026, 3, 10)));
        Assert.StartsWith("۱۴۰۵/", TimeText.Relative(Now.AddDays(30), Now, persian: true));
    }

    [Fact]
    public void Elapsed_is_compact()
    {
        Assert.Equal("42 s", TimeText.Elapsed(TimeSpan.FromSeconds(42)));
        Assert.Equal("3:05", TimeText.Elapsed(TimeSpan.FromSeconds(185)));
        Assert.Equal("1:02:03", TimeText.Elapsed(new TimeSpan(1, 2, 3)));
    }

    [Fact]
    public void Job_state_shows_what_needs_attention_first()
    {
        var job = new BackupJob();
        var failed = new BackupRun { Status = RunStatus.Failed };

        Assert.Equal(JobState.Running, JobStates.Of(job, failed, running: true, paused: false));
        Assert.Equal(JobState.Paused, JobStates.Of(job, failed, running: true, paused: true));
        Assert.Equal(JobState.Failed, JobStates.Of(job, failed, running: false, paused: false));
        Assert.Equal(JobState.Never, JobStates.Of(job, null, running: false, paused: false));
        Assert.Equal(JobState.Warning, JobStates.Of(job, new BackupRun { Status = RunStatus.PartiallySucceeded }, false, false));
        job.Enabled = false;
        Assert.Equal(JobState.Disabled, JobStates.Of(job, failed, running: false, paused: false));
    }
}
