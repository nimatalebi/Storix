using NT.Storix.Core;
using NT.Storix.Core.Configuration;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

public enum WelcomeChoice
{
    None,
    Gallery,
    Batch,
    Blank,
    Import,
}

/// <summary>First-run wizard shown while no job exists: four large choices and the service switch.</summary>
internal sealed class WelcomeForm : Form
{
    private readonly CheckBox _installService;

    public WelcomeForm(bool serviceInstalled)
    {
        Localizer.Attach(this);
        Text = "Welcome to Storix";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(760, 600);

        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(20, 18, 20, 6), WrapContents = false };
        header.Controls.Add(new PictureBox { Image = Branding.Logo(LogicalToDeviceUnits(56)), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, 14, 0) });
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        titles.Controls.Add(new Label { Text = "Let's set up your first backup", AutoSize = true, Font = new Font(Font.FontFamily, 14, FontStyle.Bold) });
        titles.Controls.Add(new Label
        {
            Text = "Everything can be changed afterwards.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });
        header.Controls.Add(titles);

        var choices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(16, 8, 16, 8) };
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        choices.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        choices.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        choices.Controls.Add(Card(
            "Start from a template",
            "Ready-made jobs for websites, databases, file servers and off-site copies, with sensible schedules and retention.",
            [Localizer.T(JobTemplate.Websites), Localizer.T(JobTemplate.Databases), Localizer.T("Folders")],
            Glyphs.Template, Color.FromArgb(0x25, 0x63, 0xEB), WelcomeChoice.Gallery, featured: true), 0, 0);
        choices.Controls.Add(Card(
            "Set up several backups at once",
            "Pick websites, databases or folders from this server and get one job for each.",
            ["IIS", "MongoDB", "SQL Server"],
            Glyphs.Add, Color.FromArgb(0x7C, 0x3A, 0xED), WelcomeChoice.Batch), 1, 0);
        choices.Controls.Add(Card(
            "Empty job",
            "Choose the source, destination and schedule yourself.",
            [],
            Glyphs.Edit, Color.FromArgb(0x0E, 0x94, 0x8F), WelcomeChoice.Blank), 0, 1);
        choices.Controls.Add(Card(
            "Import configuration",
            "Load jobs and destinations exported from another machine.",
            [],
            '\uE8B5', Color.FromArgb(0xD9, 0x77, 0x06), WelcomeChoice.Import), 1, 1);

        _installService = new CheckBox
        {
            Text = "Install and start the Storix service now (runs the backups)",
            AutoSize = true,
            Checked = !serviceInstalled,
            Enabled = !serviceInstalled,
            Margin = new Padding(0, 8, 0, 0),
        };
        var skip = Ui.Button("Skip", (_, _) => Finish(WelcomeChoice.None), 90);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(20, 6, 20, 14) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_installService, 0, 0);
        footer.Controls.Add(skip, 1, 0);
        footer.Controls.Add(new Label { Text = $"Storix {StorixInfo.Version} · {StorixInfo.RepositoryUrl}", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 0) }, 0, 1);

        Controls.Add(choices);
        Controls.Add(header);
        Controls.Add(footer);
        CancelButton = skip;
    }

    public WelcomeChoice Choice { get; private set; }

    public bool InstallService => _installService.Enabled && _installService.Checked;

    private TemplateCard Card(string title, string description, IReadOnlyList<string> tags, char glyph, Color color, WelcomeChoice choice, bool featured = false)
    {
        var card = new TemplateCard(Localizer.T(title), Localizer.T(description), tags, glyph, color, featured)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(LogicalToDeviceUnits(6)),
        };

        // One click is enough here: each card is a single action, not a selection.
        card.Click += (_, _) => Finish(choice);
        card.Activated += (_, _) => Finish(choice);
        return card;
    }

    private void Finish(WelcomeChoice choice)
    {
        Choice = choice;
        DialogResult = DialogResult.OK;
        Close();
    }
}
