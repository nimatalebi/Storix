using NT.Storix.Core;
using NT.Storix.Core.Monitoring;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

/// <summary>
/// Collects feedback and hands it to GitHub Issues or the user's mail client, pre-filled.
/// Nothing is sent from the application itself.
/// </summary>
internal sealed class FeedbackForm : Form
{
    private readonly ComboBox _kind = Ui.EnumCombo(FeedbackKind.BugReport);
    private readonly TextBox _title = new();
    private readonly TextBox _message = Ui.Multiline(180);
    private readonly TextBox _contact = new();
    private readonly CheckBox _systemInfo = new() { Text = "Include version and system information", AutoSize = true, Checked = true };
    private readonly Label _systemPreview = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(520, 0) };

    public FeedbackForm()
    {
        Localizer.Attach(this);
        Text = "Send feedback";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 560);
        MinimumSize = new Size(560, 500);

        _message.WordWrap = true;
        _systemPreview.Text = StorixInfo.SystemSummary;
        _systemInfo.CheckedChanged += (_, _) => _systemPreview.Visible = _systemInfo.Checked;

        var grid = Ui.Form();
        grid.Row(null, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Text = "Found a bug or have an idea? Tell us. Choose GitHub (public, recommended) or e-mail. " +
                   "Your browser or mail client opens with the message filled in, so you can review it before sending. " +
                   "Please do not include passwords or connection strings.",
        });
        grid.Row("Type", _kind);
        grid.Row("Title", _title);
        grid.Row("Message", _message, height: 190);
        grid.Row("Your e-mail (optional)", _contact);
        grid.Row(null, _systemInfo);
        grid.Row(null, _systemPreview);
        grid.Row(null, Ui.Buttons(
            Ui.Button("Open GitHub issue", (_, _) => Submit(FeedbackComposer.BuildIssueUrl), 140),
            Ui.Button("Send e-mail", (_, _) => Submit(FeedbackComposer.BuildMailtoUrl), 110),
            Ui.Button("Copy text", (_, _) => CopyText(), 100),
            Ui.Button("Close", (_, _) => Close(), 90)));
        grid.Row(null, new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = $"GitHub: {StorixInfo.IssuesUrl}    E-mail: {StorixInfo.ContactEmail}",
        });
        grid.Fill();

        Controls.Add(grid);
    }

    private Feedback? Read()
    {
        if (string.IsNullOrWhiteSpace(_title.Text) && string.IsNullOrWhiteSpace(_message.Text))
        {
            Dialogs.Error(this, "Please enter a title or a message.");
            return null;
        }

        return new Feedback((FeedbackKind)_kind.SelectedItem!, _title.Text, _message.Text, _contact.Text, _systemInfo.Checked);
    }

    private void Submit(Func<Feedback, string> buildUrl)
    {
        if (Read() is { } feedback)
        {
            Links.Open(this, buildUrl(feedback));
        }
    }

    private void CopyText()
    {
        if (Read() is { } feedback)
        {
            Clipboard.SetText($"{FeedbackComposer.Title(feedback)}{Environment.NewLine}{Environment.NewLine}{FeedbackComposer.Body(feedback).ReplaceLineEndings(Environment.NewLine)}");
            Dialogs.Info(this, "The feedback text was copied to the clipboard.");
        }
    }
}
