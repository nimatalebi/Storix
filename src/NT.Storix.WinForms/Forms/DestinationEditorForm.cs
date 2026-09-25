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
    private readonly ComboBox _kind;
    private readonly PropertyGrid _grid = new() { ToolbarVisible = false, PropertySort = PropertySort.Categorized, HelpVisible = true };
    private readonly Button _test;
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Anchor = AnchorStyles.Left };
    private readonly Label _presetHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(480, 0) };

    public DestinationEditorForm(DestinationDefinition destination, IDestinationFactory factory)
    {
        _factory = factory;
        Destination = StorixJson.Clone(destination);

        Text = "Destination";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(560, 520);
        MinimumSize = new Size(480, 420);

        _kind = Ui.EnumCombo(Destination.Kind);
        _name.Text = Destination.Name;
        _enabled.Checked = Destination.Enabled;
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
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };

        var grid = Ui.Form();
        grid.Row("Name", _name);
        grid.Row("Type", _kind);
        grid.Row(null, _enabled);
        grid.Row("S3 provider", _preset);
        grid.Row(null, _presetHint);
        grid.Row(null, _grid, height: 330);
        grid.Row(null, Ui.Buttons(_test, ok, cancel));
        grid.Fill();

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(grid);
        UpdatePresetVisibility();
    }

    public DestinationDefinition Destination { get; }

    private void UpdatePresetVisibility()
    {
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
