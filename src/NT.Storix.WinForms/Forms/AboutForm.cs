using NT.Storix.Core;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

/// <summary>About Storix: version, license, project links and contact.</summary>
internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "About Storix";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 430);

        var grid = Ui.Form();
        grid.Padding = new Padding(20);

        grid.Row(null, new Label { Text = StorixInfo.ProductName, AutoSize = true, Font = new Font(Font.FontFamily, 22, FontStyle.Bold) });
        grid.Row(null, new Label { Text = $"Version {StorixInfo.Version}", AutoSize = true, ForeColor = SystemColors.GrayText });
        grid.Row(null, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(470, 0),
            Margin = new Padding(3, 12, 3, 12),
            Text = "Storix is a free, open-source backup agent for Windows. It backs up files, SQL Server and MongoDB " +
                   "on a schedule, compresses and encrypts them, and stores them on local folders, FTP, SFTP and Google Drive.",
        });

        grid.Row("GitHub", Link(StorixInfo.RepositoryUrl, StorixInfo.RepositoryUrl));
        grid.Row("Issues", Link("Report a bug or request a feature", StorixInfo.IssuesUrl));
        grid.Row("Contact", Link(StorixInfo.ContactEmail, $"mailto:{StorixInfo.ContactEmail}"));
        grid.Row("License", Link($"{StorixInfo.License} License", StorixInfo.LicenseUrl));

        grid.Row(null, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(470, 0),
            Margin = new Padding(3, 12, 3, 3),
            ForeColor = SystemColors.GrayText,
            Text = $"Copyright (c) {StorixInfo.Authors}. You are free to use, modify and share Storix, including commercially. " +
                   "Contributions and feedback are very welcome.",
        });

        var feedback = Ui.Button("Send feedback...", (_, _) =>
        {
            using var form = new FeedbackForm();
            form.ShowDialog(this);
        }, 130);
        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        grid.Row(null, Ui.Buttons(feedback, close));
        grid.Fill();

        AcceptButton = close;
        CancelButton = close;
        Controls.Add(grid);
    }

    private LinkLabel Link(string text, string url)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
        link.LinkClicked += (_, _) => Links.Open(this, url);
        return link;
    }
}
