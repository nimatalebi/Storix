using NT.Storix.Core;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

internal sealed class DestinationEditorForm : Form
{
    private readonly IDestinationFactory _factory;
    private readonly TextBox _name = new();
    private readonly CheckBox _enabled = new() { Text = "Enabled", AutoSize = true };
    private readonly NumericUpDown _limit = Ui.Number(0, 10_000_000);
    private readonly ComboBox _kind;
    private readonly PropertyGrid _grid = new() { ToolbarVisible = false, PropertySort = PropertySort.Categorized, HelpVisible = true };
    private readonly Button _test;
    private readonly Button _googleSignIn;
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Anchor = AnchorStyles.Left };
    private readonly Label _presetHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(480, 0) };
    private readonly GuidePanel _guide = new() { Dock = DockStyle.Fill };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(520, 0), Padding = new Padding(0, 4, 0, 0) };
    private readonly Button _browseKey;

    /// <summary>Friendly names for the type list.</summary>
    private static string KindName(DestinationKind kind) => kind switch
    {
        DestinationKind.LocalFolder => "Local folder / NAS / network share",
        DestinationKind.Ftp => "FTP / FTPS",
        DestinationKind.Sftp => "SFTP (SSH)",
        DestinationKind.GoogleDrive => "Google Drive",
        DestinationKind.S3 => "Amazon S3 / S3-compatible (R2, Wasabi, B2, MinIO, Arvan)",
        DestinationKind.AzureBlob => "Azure Blob Storage",
        DestinationKind.WebDav => "WebDAV (Nextcloud, ownCloud, NAS)",
        DestinationKind.Dropbox => "Dropbox",
        DestinationKind.OneDrive => "OneDrive / SharePoint",
        DestinationKind.Rclone => "rclone (40+ providers)",
        DestinationKind.Telegram => "Telegram / Bale channel",
        DestinationKind.Plugin => "Plugin",
        _ => kind.ToString(),
    };

    public DestinationEditorForm(DestinationDefinition destination, IDestinationFactory factory)
    {
        Localizer.Attach(this);
        _factory = factory;
        Destination = StorixJson.Clone(destination);

        Text = "Destination";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(1040, 640);
        MinimumSize = new Size(820, 480);

        _kind = Ui.EnumCombo(Destination.Kind);
        _kind.Width = 360;
        _kind.Format += (_, e) => e.Value = e.ListItem is DestinationKind k ? Localizer.T(KindName(k)) : e.Value;
        _name.Text = Destination.Name;
        _enabled.Checked = Destination.Enabled;
        _limit.Value = Math.Clamp(Destination.MaxUploadKBps, 0, 10_000_000);
        _grid.SelectedObject = Destination.ActiveOptions;
        _preset.Items.Add("(choose a provider)");
        _preset.Items.AddRange(S3Preset.All.Cast<object>().ToArray());
        _preset.SelectedIndex = 0;
        _preset.SelectedIndexChanged += (_, _) =>
        {
            if (_preset.SelectedItem is S3Preset preset)
            {
                preset.ApplyTo(Destination.S3);
                _presetHint.Text = preset.Hint;
                _grid.Refresh();
            }
        };

        _kind.SelectedIndexChanged += (_, _) =>
        {
            Destination.Kind = (DestinationKind)_kind.SelectedItem!;
            _grid.SelectedObject = Destination.ActiveOptions;
            UpdatePresetVisibility();
        };

        // The Google Drive guide depends on the sign-in mode.
        _grid.PropertyValueChanged += (_, _) => UpdatePresetVisibility();

        _test = Ui.Button("Test connection", OnTest, 130);
        _googleSignIn = Ui.Button("Sign in...", OnSignIn, 170);
        _browseKey = Ui.Button("Choose key file...", OnBrowseKey, 150);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };

        var grid = Ui.Form();
        grid.Row("Name", _name);
        grid.Row("Type", _kind);
        grid.Row(null, _enabled);
        grid.Row("Upload limit (KB/s, 0 = unlimited)", _limit);
        grid.Row("S3 provider", _preset);
        grid.Row(null, _presetHint);
        grid.Row(null, _grid, height: 330);
        grid.Row(null, Ui.Buttons(_googleSignIn, _browseKey, _test));
        grid.Row(null, _result);
        grid.Fill();

        // Settings on the left, the setup guide for the chosen type on the right.
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(grid);
        split.Panel2.Controls.Add(_guide);
        split.Panel2.Padding = new Padding(0, 10, 10, 10);
        Load += (_, _) => split.SplitterDistance = Math.Max(420, ClientSize.Width - 430);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        buttons.Controls.AddRange([cancel, ok]);

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(split);
        Controls.Add(buttons);
        UpdatePresetVisibility();
    }

    public DestinationDefinition Destination { get; }

    private async void OnSignIn(object? sender, EventArgs e)
    {
        switch (Destination.Kind)
        {
            case DestinationKind.GoogleDrive:
                await GoogleSignInAsync();
                break;
            case DestinationKind.Dropbox:
                await OAuthSignInAsync(
                    string.IsNullOrWhiteSpace(Destination.Dropbox.AppKey) ? null : Destination.Dropbox.AppKey,
                    "Enter the app key of your Dropbox app first (dropbox.com/developers/apps, redirect URI http://localhost:53682/).",
                    ct => DropboxDestination.SignInAsync(Destination.Dropbox.AppKey, ct),
                    token => Destination.Dropbox.RefreshToken = token);
                break;
            case DestinationKind.OneDrive:
                await OAuthSignInAsync(
                    string.IsNullOrWhiteSpace(Destination.OneDrive.ClientId) ? null : Destination.OneDrive.ClientId,
                    "Enter the client id of an Azure app registration first (public client, redirect URI http://localhost:53682/, permission Files.ReadWrite).",
                    ct => OneDriveDestination.SignInAsync(Destination.OneDrive.ClientId, Destination.OneDrive.Tenant, ct),
                    token => Destination.OneDrive.RefreshToken = token);
                break;
        }
    }

    private async Task OAuthSignInAsync(string? clientId, string missingMessage, Func<CancellationToken, Task<NT.Storix.Core.Security.OAuthTokens>> signIn, Action<string> store)
    {
        if (clientId is null)
        {
            ShowResult(false, missingMessage);
            return;
        }

        _googleSignIn.Enabled = false;
        UseWaitCursor = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var tokens = await signIn(timeout.Token);
            store(tokens.RefreshToken ?? throw new InvalidOperationException("The provider did not return a refresh token."));
            _grid.Refresh();
            ShowResult(true, "Connected. Use 'Test connection' to check the folder.");
        }
        catch (Exception ex)
        {
            ShowResult(false, $"Sign-in failed: {ex.Message}");
        }
        finally
        {
            UseWaitCursor = false;
            _googleSignIn.Enabled = true;
        }
    }

    private async Task GoogleSignInAsync()
    {
        var drive = Destination.GoogleDrive;
        if (string.IsNullOrWhiteSpace(drive.OAuthClientId) || string.IsNullOrWhiteSpace(drive.OAuthClientSecret))
        {
            ShowResult(false, "Enter the OAuth client id and client secret first (see the setup guide on the right, steps 1-5).");
            return;
        }

        _googleSignIn.Enabled = false;
        UseWaitCursor = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var (token, email) = await GoogleDriveAuth.SignInAsync(drive.OAuthClientId, drive.OAuthClientSecret, timeout.Token);
            drive.AuthMode = GoogleDriveAuthMode.UserAccount;
            drive.RefreshToken = token;
            drive.SignedInAs = email;
            _grid.Refresh();
            ShowResult(true, $"Signed in as {email ?? "your Google account"}.");
        }
        catch (Exception ex)
        {
            ShowResult(false, $"Google sign-in failed: {ex.Message}");
        }
        finally
        {
            UseWaitCursor = false;
            _googleSignIn.Enabled = true;
        }
    }

    private void UpdatePresetVisibility()
    {
        _googleSignIn.Visible = Destination.Kind is DestinationKind.GoogleDrive or DestinationKind.Dropbox or DestinationKind.OneDrive;
        _googleSignIn.Text = Localizer.T(Destination.Kind switch
        {
            DestinationKind.GoogleDrive => "Sign in with Google...",
            DestinationKind.Dropbox => "Sign in with Dropbox...",
            _ => "Sign in with Microsoft...",
        });
        _browseKey.Visible = Destination.Kind == DestinationKind.Sftp
                             || (Destination.Kind == DestinationKind.GoogleDrive && Destination.GoogleDrive.AuthMode == GoogleDriveAuthMode.ServiceAccount);
        _guide.ShowGuide(DestinationGuides.For(Destination));
        var visible = Destination.Kind == DestinationKind.S3;
        _preset.Visible = _presetHint.Visible = visible;
        if (_preset.Parent is TableLayoutPanel table && table.GetControlFromPosition(0, table.GetRow(_preset)) is { } label)
        {
            label.Visible = visible;
        }
    }

    private void OnBrowseKey(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = Localizer.T("Choose key file..."),
            Filter = Destination.Kind == DestinationKind.GoogleDrive ? "Service account key (*.json)|*.json|All files (*.*)|*.*" : "Private key|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (Destination.Kind == DestinationKind.GoogleDrive)
        {
            Destination.GoogleDrive.ServiceAccountKeyPath = dialog.FileName;
        }
        else
        {
            Destination.Sftp.PrivateKeyPath = dialog.FileName;
        }

        _grid.Refresh();
    }

    /// <summary>Shows the result of a test or sign-in under the buttons instead of a message box.</summary>
    private void ShowResult(bool success, string message)
    {
        _result.ForeColor = success ? Color.ForestGreen : Color.Firebrick;
        _result.Text = (success ? "✓ " : "✗ ") + Localizer.T(message);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            if (string.IsNullOrWhiteSpace(_name.Text))
            {
                ShowResult(false, "Enter a name.");
            _name.Focus();
                e.Cancel = true;
                return;
            }

            Apply();
        }

        base.OnFormClosing(e);
    }

    private void Apply()
    {
        Destination.Name = _name.Text.Trim();
        Destination.Enabled = _enabled.Checked;
        Destination.MaxUploadKBps = (int)_limit.Value;
        Destination.Kind = (DestinationKind)_kind.SelectedItem!;
    }

    private async void OnTest(object? sender, EventArgs e)
    {
        Apply();
        _test.Enabled = false;
        UseWaitCursor = true;
        _result.ForeColor = SystemColors.GrayText;
        _result.Text = Localizer.T("Testing the connection...");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await Task.Run(async () =>
            {
                await using var destination = _factory.Create(Destination);
                await destination.TestAsync(timeout.Token);
                await destination.ListAsync(timeout.Token);
            });
            ShowResult(true, "Connection succeeded.");
        }
        catch (Exception ex)
        {
            ShowResult(false, $"Connection failed: {ex.Message}");
        }
        finally
        {
            UseWaitCursor = false;
            _test.Enabled = true;
        }
    }
}
