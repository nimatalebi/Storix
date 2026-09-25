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
        _kind.SelectedIndexChanged += (_, _) =>
        {
            Destination.Kind = (DestinationKind)_kind.SelectedItem!;
            _grid.SelectedObject = Destination.ActiveOptions;
        };

        _test = Ui.Button("Test connection", OnTest, 130);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };

        var grid = Ui.Form();
        grid.Row("Name", _name);
        grid.Row("Type", _kind);
        grid.Row(null, _enabled);
        grid.Row(null, _grid, height: 330);
        grid.Row(null, Ui.Buttons(_test, ok, cancel));
        grid.Fill();

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(grid);
    }

    public DestinationDefinition Destination { get; }

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
