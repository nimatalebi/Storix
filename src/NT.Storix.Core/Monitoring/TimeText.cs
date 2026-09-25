using System.Globalization;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Monitoring;

/// <summary>Short, human-friendly texts for the user interface ("5 min ago", "in 3 h", "Today 22:00").</summary>
public static class TimeText
{
    /// <summary>Relative text for times close to <paramref name="now"/>, a day and time otherwise (local time).</summary>
    /// <param name="persian">Persian wording, digits and Solar Hijri dates.</param>
    public static string Relative(DateTimeOffset when, DateTimeOffset now, bool persian = false)
    {
        var delta = when - now;
        var future = delta > TimeSpan.Zero;
        var span = delta.Duration();
        if (span < TimeSpan.FromMinutes(1))
        {
            return persian ? future ? "کمتر از یک دقیقهٔ دیگر" : "همین الان" : future ? "in less than a minute" : "just now";
        }

        if (span < TimeSpan.FromHours(1))
        {
            var minutes = (int)span.TotalMinutes;
            return persian
                ? future ? $"{Digits(minutes)} دقیقهٔ دیگر" : $"{Digits(minutes)} دقیقه پیش"
                : future ? $"in {minutes} min" : $"{minutes} min ago";
        }

        if (span < TimeSpan.FromHours(6))
        {
            var hours = (int)span.TotalHours;
            return persian
                ? future ? $"{Digits(hours)} ساعت دیگر" : $"{Digits(hours)} ساعت پیش"
                : future ? $"in {hours} h" : $"{hours} h ago";
        }

        return Day(when, now, persian);
    }

    /// <summary>"Today 22:00", "Tomorrow 03:00", "Yesterday 21:15", "Mon 08:00" (within a week), else "2026-03-05 22:00".</summary>
    public static string Day(DateTimeOffset when, DateTimeOffset now, bool persian = false)
    {
        var local = when.ToLocalTime();
        var days = (local.Date - now.ToLocalTime().Date).Days;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (persian)
        {
            time = Digits(time);
            return days switch
            {
                0 => $"امروز {time}",
                1 => $"فردا {time}",
                -1 => $"دیروز {time}",
                > 1 and < 7 => $"{PersianDays[(int)local.DayOfWeek]} {time}",
                _ => $"{Digits(SolarHijri(local.DateTime))} {time}",
            };
        }

        return days switch
        {
            0 => $"Today {time}",
            1 => $"Tomorrow {time}",
            -1 => $"Yesterday {time}",
            > 1 and < 7 => $"{local.ToString("ddd", CultureInfo.InvariantCulture)} {time}",
            _ => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        };
    }

    private static readonly string[] PersianDays = ["یکشنبه", "دوشنبه", "سه‌شنبه", "چهارشنبه", "پنجشنبه", "جمعه", "شنبه"];

    /// <summary>Date in the Solar Hijri (Persian) calendar, e.g. 1404/12/19.</summary>
    public static string SolarHijri(DateTime date)
    {
        var calendar = new PersianCalendar();
        return $"{calendar.GetYear(date):0000}/{calendar.GetMonth(date):00}/{calendar.GetDayOfMonth(date):00}";
    }

    /// <summary>Western digits to Persian digits.</summary>
    public static string Digits(object value) =>
        string.Concat(Convert.ToString(value, CultureInfo.InvariantCulture)!.Select(c => c is >= '0' and <= '9' ? (char)('۰' + (c - '0')) : c));

    /// <summary>Elapsed time as "42 s", "3:05" or "1:02:03".</summary>
    public static string Elapsed(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? $"{(int)span.TotalSeconds} s"
        : span < TimeSpan.FromHours(1) ? span.ToString(@"m\:ss", CultureInfo.InvariantCulture)
        : $"{(int)span.TotalHours}:{span:mm\\:ss}";
}

public enum JobState
{
    Never,
    Ok,
    Warning,
    Failed,
    Running,
    Paused,
    Disabled,
}

/// <summary>The one state shown for a job in lists (what needs attention first).</summary>
public static class JobStates
{
    public static JobState Of(BackupJob job, BackupRun? last, bool running, bool paused) =>
        running ? paused ? JobState.Paused : JobState.Running
        : !job.Enabled ? JobState.Disabled
        : paused ? JobState.Paused
        : last?.Status switch
        {
            null => JobState.Never,
            RunStatus.Succeeded => JobState.Ok,
            RunStatus.PartiallySucceeded or RunStatus.Cancelled => JobState.Warning,
            RunStatus.Running => JobState.Running,
            _ => JobState.Failed,
        };

    public static string Describe(JobState state) => state switch
    {
        JobState.Never => "Not run yet",
        JobState.Ok => "OK",
        JobState.Warning => "Needs attention",
        JobState.Failed => "Failed",
        JobState.Running => "Running",
        JobState.Paused => "Paused",
        _ => "Disabled",
    };
}
