using System.Globalization;

namespace NT.Storix.Core.Scheduling;

/// <summary>A daily time window (e.g. 22:00-06:00) during which uploads are allowed.</summary>
public sealed record UploadWindow(TimeSpan Start, TimeSpan End)
{
    /// <summary>Parses "HH:mm-HH:mm". Returns null for empty input.</summary>
    /// <exception cref="FormatException">Invalid format.</exception>
    public static UploadWindow? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !TimeSpan.TryParseExact(parts[0], @"h\:mm", CultureInfo.InvariantCulture, out var start)
            || !TimeSpan.TryParseExact(parts[1], @"h\:mm", CultureInfo.InvariantCulture, out var end)
            || start >= TimeSpan.FromDays(1) || end > TimeSpan.FromDays(1) || start == end)
        {
            throw new FormatException("Upload window must look like 22:00-06:00.");
        }

        return new UploadWindow(start, end);
    }

    public bool Contains(TimeSpan timeOfDay) =>
        Start < End
            ? timeOfDay >= Start && timeOfDay < End
            : timeOfDay >= Start || timeOfDay < End; // Overnight window.

    /// <summary>Returns how long to wait until uploads are allowed (zero when inside the window).</summary>
    public TimeSpan Delay(DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        if (Contains(local.TimeOfDay))
        {
            return TimeSpan.Zero;
        }

        var startToday = local.Date + Start;
        var nextStart = startToday > local.DateTime ? startToday : startToday.AddDays(1);
        return nextStart - local.DateTime;
    }

    public override string ToString() => $"{Start:hh\\:mm}-{End:hh\\:mm}";
}
