using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace NT.Storix.WinForms.Infrastructure;

/// <summary>
/// A clickable card: colored icon, title, description and small tags. Owner-drawn so it looks the same in
/// light and dark mode and mirrors itself for right-to-left languages.
/// </summary>
internal sealed class TemplateCard : Control
{
    private readonly char _glyph;
    private readonly IReadOnlyList<string> _tags;
    private bool _hover;
    private bool _selected;

    public TemplateCard(string title, string description, IReadOnlyList<string> tags, char glyph, Color accent, bool featured = false)
    {
        Text = title;
        Description = description;
        _tags = tags;
        _glyph = glyph;
        Accent = accent;
        Featured = featured;
        DoubleBuffered = true;
        TabStop = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = title;
        AccessibleDescription = description;
    }

    public string Description { get; }

    public Color Accent { get; }

    public bool Featured { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? Value { get; init; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Invalidate();
        }
    }

    /// <summary>Double-click or Enter.</summary>
    public event EventHandler? Activated;

    /// <summary>Clicked or reached with the keyboard.</summary>
    public event EventHandler? Picked;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        Picked?.Invoke(this, EventArgs.Empty);
        base.OnMouseDown(e);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        base.OnDoubleClick(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            Activated?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Picked?.Invoke(this, EventArgs.Empty);
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? SystemColors.Control);

        var rtl = RightToLeft == RightToLeft.Yes;
        var pad = LogicalToDeviceUnits(14);
        var iconSize = LogicalToDeviceUnits(44);
        var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
        var window = SystemColors.Window;
        var background = Featured ? Blend(window, Accent, 0.10f) : _hover ? Blend(window, Accent, 0.05f) : window;
        var border = _selected || Focused ? Accent : _hover ? Blend(SystemColors.ControlDark, Accent, 0.5f) : Blend(window, SystemColors.ControlDark, 0.45f);

        using (var path = Rounded(bounds, LogicalToDeviceUnits(10)))
        using (var fill = new SolidBrush(background))
        using (var pen = new Pen(border, _selected || Focused ? LogicalToDeviceUnits(2) : 1))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        // Icon: glyph in a colored circle (first letter when the icon font is missing).
        var iconX = rtl ? Width - pad - iconSize : pad;
        using (var circle = new SolidBrush(Accent))
        {
            g.FillEllipse(circle, iconX, pad, iconSize, iconSize);
        }

        var glyph = Glyphs.Icon(_glyph, LogicalToDeviceUnits(24), Color.White);
        if (glyph is not null)
        {
            g.DrawImage(glyph, iconX + ((iconSize - glyph.Width) / 2), pad + ((iconSize - glyph.Height) / 2));
        }
        else
        {
            TextRenderer.DrawText(g, Text[..1], new Font(Font.FontFamily, 14, FontStyle.Bold), new Rectangle(iconX, pad, iconSize, iconSize), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        var textLeft = rtl ? pad : pad + iconSize + pad;
        var textWidth = Width - iconSize - (3 * pad);
        var align = rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left;

        using var titleFont = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold);
        var titleHeight = TextRenderer.MeasureText(g, Text, titleFont, new Size(textWidth, 0), TextFormatFlags.WordBreak).Height;
        titleHeight = Math.Min(titleHeight, titleFont.Height * 2);
        TextRenderer.DrawText(g, Text, titleFont, new Rectangle(textLeft, pad - 2, textWidth, titleHeight), SystemColors.WindowText,
            align | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        // Tags along the bottom, the description in between.
        var tagHeight = Font.Height + LogicalToDeviceUnits(6);
        var tagsTop = Height - pad - tagHeight;
        var descriptionTop = pad + titleHeight + LogicalToDeviceUnits(4);
        TextRenderer.DrawText(g, Description, Font, new Rectangle(textLeft, descriptionTop, textWidth, Math.Max(0, tagsTop - descriptionTop - LogicalToDeviceUnits(6))),
            Blend(SystemColors.WindowText, background, 0.35f), align | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        var x = rtl ? textLeft + textWidth : textLeft;
        using var tagFont = new Font(Font.FontFamily, Font.Size - 0.5f);
        foreach (var tag in _tags)
        {
            var width = TextRenderer.MeasureText(g, tag, tagFont).Width + LogicalToDeviceUnits(12);
            var left = rtl ? x - width : x;
            if (left < textLeft - 1 || left + width > textLeft + textWidth + 1)
            {
                break;
            }

            var pill = new Rectangle(left, tagsTop, width, tagHeight);
            using (var path = Rounded(pill, tagHeight / 2))
            using (var fill = new SolidBrush(Blend(background, Accent, 0.16f)))
            {
                g.FillPath(fill, path);
            }

            TextRenderer.DrawText(g, tag, tagFont, pill, Blend(Accent, SystemColors.WindowText, 0.35f),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | (rtl ? TextFormatFlags.RightToLeft : 0));
            x = rtl ? left - LogicalToDeviceUnits(6) : left + width + LogicalToDeviceUnits(6);
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)(a.R + ((b.R - a.R) * amount)),
        (int)(a.G + ((b.G - a.G) * amount)),
        (int)(a.B + ((b.B - a.B) * amount)));
}
