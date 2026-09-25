using System.Drawing.Drawing2D;
using System.Drawing.Text;
using NT.Storix.Core.Monitoring;

namespace NT.Storix.WinForms.Infrastructure;

/// <summary>
/// Toolbar icons drawn from the Windows icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on
/// Windows 10), so they match the system and scale with DPI. Without the font, buttons simply show text.
/// </summary>
internal static class Glyphs
{
    public const char Add = '';
    public const char Edit = '';
    public const char Copy = '';
    public const char Delete = '';
    public const char Play = '';
    public const char Pause = '';
    public const char Stop = '';
    public const char Refresh = '';
    public const char History = '';
    public const char Health = '';
    public const char Preview = '';
    public const char More = '';
    public const char Template = '';
    public const char Power = '';
    public const char Search = '';

    private static readonly string? FontName = new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }
        .FirstOrDefault(name => new InstalledFontCollection().Families.Any(f => f.Name == name));

    private static readonly Dictionary<(char, int, int), Image> Cache = [];

    /// <summary>A glyph as an image of <paramref name="size"/> pixels, or null when the icon font is missing.</summary>
    public static Image? Icon(char glyph, int size = 16, Color? color = null)
    {
        if (FontName is null)
        {
            return null;
        }

        var tint = color ?? SystemColors.ControlText;
        var key = (glyph, size, tint.ToArgb());
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font(FontName, size * 0.75f, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(tint))
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var text = glyph.ToString();
            var measured = graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            graphics.DrawString(text, font, brush, (size - measured.Width) / 2, (size - measured.Height) / 2, StringFormat.GenericTypographic);
        }

        Cache[key] = bitmap;
        return bitmap;
    }

    public static Color StateColor(JobState state) => state switch
    {
        JobState.Ok => Color.FromArgb(0x10, 0x8A, 0x3E),
        JobState.Warning => Color.FromArgb(0xD1, 0x7A, 0x00),
        JobState.Failed => Color.FromArgb(0xC4, 0x2B, 0x1C),
        JobState.Running => Color.FromArgb(0x00, 0x67, 0xC0),
        JobState.Paused => Color.FromArgb(0x8A, 0x6D, 0x00),
        _ => Color.Gray,
    };

    /// <summary>Round status dots for list rows (one per <see cref="JobState"/>, keyed by its name).</summary>
    public static ImageList StateImages(int size)
    {
        var list = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
        foreach (var state in Enum.GetValues<JobState>())
        {
            var bitmap = new Bitmap(size, size);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(StateColor(state)))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var inset = size / 4f;
                if (state is JobState.Never or JobState.Disabled)
                {
                    using var pen = new Pen(brush, Math.Max(1.5f, size / 10f));
                    graphics.DrawEllipse(pen, inset, inset, size - (2 * inset), size - (2 * inset));
                }
                else
                {
                    graphics.FillEllipse(brush, inset, inset, size - (2 * inset), size - (2 * inset));
                }
            }

            list.Images.Add(state.ToString(), bitmap);
        }

        return list;
    }
}
