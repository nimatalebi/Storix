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

    public DestinationEditorForm(DestinationDefinition destination, IDestinationFactory factory)
    {
        _factory = factory;
        Destination = StorixJson.Clone(destination);

        Text = "Destination";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(560, 560);
        MinimumSize = new Size(480, 420);

        _kind = Ui.EnumCombo(Destination.Kind);
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

        _test = Ui.Button("Test connection", OnTest, 130);
        _googleSignIn = Ui.Button("Sign in with Google...", OnGoogleSignIn, 170);
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
        grid.Row(null, Ui.Buttons(_googleSignIn, _test, ok, cancel));
        grid.Fill();

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(grid);
        UpdatePresetVisibility();
    }

    public DestinationDefinition Destination { get; }

    private async void OnGoogleSignIn(object? sender, EventArgs e)
    {
        var drive = Destination.GoogleDrive;
        if (string.IsNullOrWhiteSpace(drive.OAuthClientId) || string.IsNullOrWhiteSpace(drive.OAuthClientSecret))
        {
            Dialogs.Error(this, "Enter the OAuth client id and client secret first.\n\nGoogle Cloud console → APIs & Services → Credentials → Create credentials → OAuth client ID → Desktop app (enable the Google Drive API).");
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
            Dialogs.Info(this, $"Signed in as {email ?? "your Google account"}.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, $"Google sign-in failed:\n\n{ex.Message}");
        }
        finally
        {
            UseWaitCursor = false;
            _googleSignIn.Enabled = true;
        }
    }

    private void UpdatePresetVisibility()
    {
        _googleSignIn.Visible = Destination.Kind == DestinationKind.GoogleDrive;
        var visible = Destination.Kind == DestinationKind.S3;
        _preset.Visible = _presetHint.Visible = visible;
        if (_preset.Parent is TableLayoutPanel table && table.GetControlFromPosition(0, table.GetRow(_preset)) is { } label)
        {
            label.Visible = visible;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            if (string.IsNullOrWhiteSpace(_name.Text))
            {
                Dialogs.Error(this, "Enter a name.");
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
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await Task.Run(async () =>
            {
                await using var destination = _factory.Create(Destination);
                await destination.TestAsync(timeout.Token);
                await destination.ListAsync(timeout.Token);
            });
            Dialogs.Info(this, "Connection succeeded.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, $"Connection failed:\n\n{ex.Message}");
        }
        finally
        {
            UseWaitCursor = false;
            _test.Enabled = true;
        }
    }
}
