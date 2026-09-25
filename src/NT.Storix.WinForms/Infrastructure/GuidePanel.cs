using NT.Storix.Core.Destinations;

namespace NT.Storix.WinForms.Infrastructure;

/// <summary>
/// Shows a setup guide: numbered steps (selectable, so URLs and values can be copied), links that open in the
/// browser and a note about pitfalls. Follows the user interface language.
/// </summary>
internal sealed class GuidePanel : Panel
{
    private DestinationGuide? _guide;

    public GuidePanel()
    {
        AutoScroll = true;
        Padding = new Padding(12, 8, 12, 8);
        BorderStyle = BorderStyle.FixedSingle;
        Resize += (_, _) =>
        {
            if (_guide is not null)
            {
                Render(_guide);
            }
        };
    }

    public void ShowGuide(DestinationGuide guide)
    {
        _guide = guide;
        Render(guide);
    }

    private void Render(DestinationGuide guide)
    {
        var persian = Localizer.IsPersian;
        var width = Math.Max(160, ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
        SuspendLayout();
        Controls.Clear();
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
        };

        flow.Controls.Add(new Label
        {
            Text = Localizer.T("Setup guide"),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 2),
        });
        flow.Controls.Add(new Label
        {
            Text = guide.Title.For(persian),
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Font = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        });

        for (var i = 0; i < guide.Steps.Count; i++)
        {
            flow.Controls.Add(Step($"{i + 1}. {guide.Steps[i].For(persian)}", width));
        }

        if (guide.Note is { } note)
        {
            var box = Step("⚠ " + note.For(persian), width);
            box.ForeColor = Color.DarkGoldenrod;
            flow.Controls.Add(box);
        }

        foreach (var link in guide.Links)
        {
            var label = new LinkLabel { Text = link.Label.For(persian) + " ↗", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            var url = link.Url;
            label.LinkClicked += (_, _) => Links.Open(FindForm(), url);
            flow.Controls.Add(label);
        }

        Controls.Add(flow);
        ResumeLayout();
    }

    /// <summary>A read-only text box that looks like a label but lets the user select and copy.</summary>
    private TextBox Step(string text, int width)
    {
        var height = TextRenderer.MeasureText(text, Font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
        return new TextBox
        {
            Text = text,
            ReadOnly = true,
            Multiline = true,
            BorderStyle = BorderStyle.None,
            BackColor = BackColor,
            TabStop = false,
            Width = width,
            Height = height + 4,
            Margin = new Padding(0, 0, 0, 8),
        };
    }
}
