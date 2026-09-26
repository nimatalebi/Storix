using NT.Storix.Core.Configuration;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

public enum GalleryChoice
{
    None,
    Template,
    Batch,
    Blank,
}

/// <summary>
/// Where a new job starts: a gallery of template cards grouped by category with search, the "several at once"
/// wizard as the first card, or an empty job.
/// </summary>
internal sealed class TemplateGalleryForm : Form
{
    private const string All = "All templates";

    private static readonly Dictionary<string, (char Glyph, Color Color)> Categories = new()
    {
        [JobTemplate.Websites] = ('', Color.FromArgb(0x25, 0x63, 0xEB)),
        [JobTemplate.Databases] = ('', Color.FromArgb(0x7C, 0x3A, 0xED)),
        [JobTemplate.Files] = ('', Color.FromArgb(0x0E, 0x94, 0x8F)),
        [JobTemplate.Server] = ('', Color.FromArgb(0xD9, 0x77, 0x06)),
        [JobTemplate.Offsite] = ('', Color.FromArgb(0xDB, 0x27, 0x77)),
    };

    private readonly FlowLayoutPanel _cards = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10), BackColor = SystemColors.Control };
    private readonly FlowLayoutPanel _categories = new() { Dock = DockStyle.Left, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 230, Padding = new Padding(10, 12, 6, 10) };
    private readonly TextBox _search = new() { Width = 280, PlaceholderText = "Search templates" };
    private readonly Label _count = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 0) };
    private readonly Button _use;
    private string _category = All;
    private TemplateCard? _selected;

    public TemplateGalleryForm()
    {
        Localizer.Attach(this);
        Text = "New backup job";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1120, 740);
        MinimumSize = new Size(780, 520);
        KeyPreview = true;

        // Header: logo, title and search.
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 84, ColumnCount = 3, Padding = new Padding(16, 12, 16, 8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var logo = new PictureBox { Image = Branding.Logo(LogicalToDeviceUnits(52)), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, 12, 0) };
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        titles.Controls.Add(new Label { Text = "Choose a starting point", AutoSize = true, Font = new Font(Font.FontFamily, Font.Size + 6, FontStyle.Bold) });
        titles.Controls.Add(new Label { Text = "Every template can be changed after it is created. Double-click a card, or select it and click \"Use template\".", AutoSize = true, ForeColor = SystemColors.GrayText });
        var searchBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Anchor = AnchorStyles.Right };
        searchBox.Controls.AddRange([_search, _count]);
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(titles, 1, 0);
        header.Controls.Add(searchBox, 2, 0);

        // Categories on the side.
        AddCategory(All, '', Color.FromArgb(0x25, 0x63, 0xEB), JobTemplate.All.Count);
        foreach (var (name, (glyph, color)) in Categories)
        {
            AddCategory(name, glyph, color, JobTemplate.All.Count(t => t.Category == name));
        }

        // Bottom: empty job, use template, cancel.
        _use = new Button { Text = "Use template", Width = 150, Height = 32, Enabled = false };
        _use.Click += (_, _) => Finish(GalleryChoice.Template);
        var blank = new Button { Text = "Empty job", Width = 120, Height = 32 };
        blank.Click += (_, _) => Finish(GalleryChoice.Blank);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 52, Padding = new Padding(12, 10, 12, 8) };
        buttons.Controls.AddRange([cancel, _use, blank]);

        Controls.Add(_cards);
        Controls.Add(_categories);
        Controls.Add(header);
        Controls.Add(buttons);
        AcceptButton = _use;
        CancelButton = cancel;

        _search.TextChanged += (_, _) => ShowCards();
        _cards.Resize += (_, _) => LayoutCards();
        Load += (_, _) =>
        {
            ShowCards();
            _search.Focus();
        };
    }

    public GalleryChoice Choice { get; private set; }

    public JobTemplate? Template { get; private set; }

    private void AddCategory(string name, char glyph, Color color, int count)
    {
        var button = new RadioButton
        {
            Text = $"{Localizer.T(name)}  ({count})",
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            Width = 210,
            Height = LogicalToDeviceUnits(38),
            TextAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Image = Glyphs.Icon(glyph, LogicalToDeviceUnits(18), color),
            ImageAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            Checked = name == All,
            Tag = name,
            Margin = new Padding(0, 0, 0, 4),
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.CheckedBackColor = SystemColors.ControlLight;
        button.CheckedChanged += (_, _) =>
        {
            if (button.Checked)
            {
                _category = name;
                ShowCards();
            }
        };
        _categories.Controls.Add(button);
    }

    private void ShowCards()
    {
        var persian = Localizer.IsPersian;
        var search = _search.Text.Trim();
        bool Matches(string text) => text.Contains(search, StringComparison.CurrentCultureIgnoreCase);

        _cards.SuspendLayout();
        foreach (Control control in _cards.Controls.Cast<Control>().ToList())
        {
            control.Dispose();
        }

        _cards.Controls.Clear();
        _selected = null;
        _use.Enabled = false;

        // The wizard comes first: it is the fastest way to set up many similar backups.
        if (_category == All && search.Length == 0)
        {
            var batch = new TemplateCard(
                Localizer.T("Set up several backups at once"),
                Localizer.T("Several websites, databases or folders at once: pick them from the server, choose the destination and schedule once, and get one job for each with staggered start times."),
                [Localizer.T("Websites"), "MongoDB", "SQL Server", Localizer.T("Folders")],
                '',
                Color.FromArgb(0x25, 0x63, 0xEB),
                featured: true);
            batch.Activated += (_, _) => Finish(GalleryChoice.Batch);
            batch.Picked += (_, _) => Select(batch);
            _cards.Controls.Add(batch);
        }

        var shown = 0;
        foreach (var template in JobTemplate.All.Where(t => _category == All || t.Category == _category))
        {
            var name = template.NameFor(persian);
            var description = template.DescriptionFor(persian);
            var job = template.Create();
            var tags = TemplateTags.For(job, persian);
            if (search.Length > 0 && !Matches(name) && !Matches(description) && !tags.Any(Matches) && !Matches(template.Name))
            {
                continue;
            }

            var (glyph, color) = Categories[template.Category];
            var card = new TemplateCard(name, description, tags, glyph, color) { Value = template };
            card.Picked += (_, _) => Select(card);
            card.Activated += (_, _) =>
            {
                Select(card);
                Finish(GalleryChoice.Template);
            };
            _cards.Controls.Add(card);
            shown++;
        }

        _count.Text = Localizer.F("{0} template(s)", shown);
        LayoutCards();
        _cards.ResumeLayout();
    }

    /// <summary>Two cards per row on wide windows, one on narrow ones.</summary>
    private void LayoutCards()
    {
        var available = _cards.ClientSize.Width - _cards.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4;
        var columns = available >= LogicalToDeviceUnits(760) ? 2 : 1;
        var width = (available / columns) - LogicalToDeviceUnits(12);
        foreach (Control card in _cards.Controls)
        {
            card.Size = new Size(Math.Max(LogicalToDeviceUnits(300), width), LogicalToDeviceUnits(158));
            card.Margin = new Padding(LogicalToDeviceUnits(6));
        }
    }

    private void Select(TemplateCard card)
    {
        if (_selected is not null)
        {
            _selected.Selected = false;
        }

        _selected = card;
        card.Selected = true;
        Template = card.Value as JobTemplate;
        _use.Enabled = true;
        _use.Text = Localizer.T(card.Value is JobTemplate ? "Use template" : "Continue");
    }

    private void Finish(GalleryChoice choice)
    {
        if (choice == GalleryChoice.Template && _selected is { Value: null })
        {
            choice = GalleryChoice.Batch;
        }

        if (choice == GalleryChoice.Template && Template is null)
        {
            return;
        }

        Choice = choice;
        DialogResult = DialogResult.OK;
        Close();
    }
}
