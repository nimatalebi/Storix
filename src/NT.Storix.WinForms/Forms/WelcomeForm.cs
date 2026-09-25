using NT.Storix.Core;
using NT.Storix.Core.Configuration;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

public enum WelcomeChoice
{
    None,
    Template,
    Blank,
    Import,
}

/// <summary>First-run wizard shown while no job exists.</summary>
internal sealed class WelcomeForm : Form
{
    private readonly ListBox _templates = new() { IntegralHeight = false };
    private readonly Label _description = new() { AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = SystemColors.GrayText };
    private readonly CheckBox _installService;

    public WelcomeForm(bool serviceInstalled)
    {
        Localizer.Attach(this);
        Text = "Welcome to Storix";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(660, 520);

        _templates.Items.AddRange(JobTemplate.All.Cast<object>().ToArray());
        _templates.Format += (_, e) => e.Value = ((JobTemplate)e.ListItem!).Name;
        _templates.SelectedIndexChanged += (_, _) => _description.Text = (_templates.SelectedItem as JobTemplate)?.Description;
        _templates.SelectedIndex = 0;
        _installService = new CheckBox { Text = "Install and start the Storix service now (runs the backups)", AutoSize = true, Checked = !serviceInstalled, Enabled = !serviceInstalled };

        var grid = Ui.Form();
        grid.Padding = new Padding(18);
        grid.Row(null, new Label { Text = "Let's set up your first backup", AutoSize = true, Font = new Font(Font.FontFamily, 14, FontStyle.Bold) });
        grid.Row(null, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Text = "Start from a template (you can change everything afterwards), create an empty job, or import a configuration exported from another machine.",
        });
        grid.Row(null, _templates, height: 150);
        grid.Row(null, _description);
        grid.Row(null, _installService);
        grid.Row(null, Ui.Buttons(
            Ui.Button("Use template", (_, _) => Finish(WelcomeChoice.Template), 130),
            Ui.Button("Empty job", (_, _) => Finish(WelcomeChoice.Blank)),
            Ui.Button("Import...", (_, _) => Finish(WelcomeChoice.Import)),
            Ui.Button("Skip", (_, _) => Finish(WelcomeChoice.None), 80)));
        grid.Row(null, new Label { Text = $"Storix {StorixInfo.Version} · {StorixInfo.RepositoryUrl}", AutoSize = true, ForeColor = SystemColors.GrayText });
        grid.Fill();
        Controls.Add(grid);
    }

    public WelcomeChoice Choice { get; private set; }

    public JobTemplate? Template => _templates.SelectedItem as JobTemplate;

    public bool InstallService => _installService.Enabled && _installService.Checked;

    private void Finish(WelcomeChoice choice)
    {
        Choice = choice;
        DialogResult = DialogResult.OK;
        Close();
    }
}
