using System.Diagnostics;
using NT.KeepstoneBackup.Core;
using NT.KeepstoneBackup.Core.Monitoring;
using NT.KeepstoneBackup.Core.Updates;
using NT.KeepstoneBackup.WinForms.Infrastructure;

namespace NT.KeepstoneBackup.WinForms.Forms;

/// <summary>Shows a newer release and installs it after checking its checksum and code signature.</summary>
internal sealed class UpdateForm : Form
{
    private readonly ReleaseInfo _release;
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Button _install;

    public UpdateForm(ReleaseInfo release)
    {
        Localizer.Attach(this);
        _release = release;
        Text = "KeepstoneBackup update";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 400);
        MinimizeBox = false;
        MaximizeBox = false;

        var grid = Ui.Form();
        grid.Row(null, new Label
        {
            Text = $"KeepstoneBackup {release.Version} is available (you have {KeepstoneBackupInfo.Version}).",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
        });
        grid.Row(null, new TextBox
        {
            Text = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes." : release.Notes.Replace("\n", Environment.NewLine),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
        }, height: 220);
        grid.Row(null, _status);
        _install = Ui.Button("Download and install", async (_, _) => await InstallAsync(), 170);
        grid.Row(null, Ui.Buttons(
            _install,
            Ui.Button("Release page", (_, _) => Links.Open(this, release.PageUrl), 120),
            Ui.Button("Later", (_, _) => Close())));
        grid.Fill();
        Controls.Add(grid);
    }

    private async Task InstallAsync()
    {
        _install.Enabled = false;
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "keepstonebackup-update");
            var progress = new Progress<string>(text => _status.Text = text);
            var installer = await UpdateChecker.DownloadAsync(SharedHttp.Client, _release, folder, progress, CancellationToken.None);
            _status.Text = "Checksum verified. Checking the code signature...";

            var signature = Authenticode.Verify(installer);
            var currentSigner = Authenticode.CurrentSigner();
            if (signature.Signed && !signature.Trusted)
            {
                File.Delete(installer);
                throw new InvalidOperationException("The installer's code signature is not valid. It was not installed.");
            }

            if (currentSigner is not null && signature.Subject != currentSigner)
            {
                File.Delete(installer);
                throw new InvalidOperationException($"The installer is signed by '{signature.Subject ?? "nobody"}', not by the publisher of this version ('{currentSigner}'). It was not installed.");
            }

            if (!signature.Signed && !Dialogs.Confirm(this,
                    "This installer is not code-signed. Its SHA-256 matches the checksum published with the release, but the " +
                    "release itself is not signed.\n\nInstall it anyway?"))
            {
                _status.Text = "Cancelled.";
                return;
            }

            // The installer upgrades in place and restarts the service; this window closes first.
            Process.Start(new ProcessStartInfo("msiexec.exe") { ArgumentList = { "/i", installer, "/passive" }, UseShellExecute = true });
            Application.Exit();
        }
        catch (Exception ex)
        {
            _status.Text = string.Empty;
            Dialogs.Error(this, ex.Message);
        }
        finally
        {
            _install.Enabled = true;
        }
    }
}
