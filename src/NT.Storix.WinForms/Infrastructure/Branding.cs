using System.Drawing.Drawing2D;
using NT.Storix.Core.Monitoring;

namespace NT.Storix.WinForms.Infrastructure;

/// <summary>The Storix logo (assets/logo.svg, embedded as an icon with sizes from 16 to 256 pixels).</summary>
internal static class Branding
{
    private static readonly Dictionary<int, Icon> TrayIcons = [];

    public static Icon AppIcon { get; } = Load();

    /// <summary>The logo at the size closest to <paramref name="size"/> pixels.</summary>
    public static Bitmap Logo(int size)
    {
        using var icon = new Icon(AppIcon, size, size);
        var bitmap = icon.ToBitmap();
        if (bitmap.Width == size)
        {
            return bitmap;
        }

        var scaled = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(bitmap, 0, 0, size, size);
        }

        bitmap.Dispose();
        return scaled;
    }

    /// <summary>The logo with a colored status dot (tray): none when all is well.</summary>
    public static Icon TrayIcon(JobState? state)
    {
        var key = state is null ? -1 : (int)state.Value;
        if (TrayIcons.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = Logo(size);
        if (state is { } badge and not JobState.Ok)
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var dot = size * 0.5f;
            var bounds = new RectangleF(size - dot, size - dot, dot, dot);
            using var border = new SolidBrush(Color.White);
            using var fill = new SolidBrush(Glyphs.StateColor(badge));
            graphics.FillEllipse(border, bounds);
            bounds.Inflate(-size / 16f, -size / 16f);
            graphics.FillEllipse(fill, bounds);
        }

        var icon = Icon.FromHandle(bitmap.GetHicon());
        TrayIcons[key] = icon;
        return icon;
    }

    private static Icon Load()
    {
        using var stream = typeof(Branding).Assembly.GetManifestResourceStream("Storix.ico");
        return stream is null ? SystemIcons.Shield : new Icon(stream);
    }
}
