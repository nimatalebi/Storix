using NT.Storix.Core;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

/// <summary>Edits a notification channel (webhook, Telegram, Bale, Slack, Teams, Discord).</summary>
internal sealed class ChannelEditorForm : Form
{
    private readonly PropertyGrid _grid = new() { ToolbarVisible = false, PropertySort = PropertySort.Categorized };

    public ChannelEditorForm(NotificationChannel channel)
    {
        Localizer.Attach(this);
        Channel = StorixJson.Clone(channel);
        Text = "Notification channel";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 480);
        MinimizeBox = false;

        _grid.SelectedObject = Channel;
        _grid.PropertyValueChanged += (_, _) => _guide.ShowGuide(NT.Storix.Core.Destinations.ChannelGuides.For(Channel.Kind));
        ClientSize = new Size(980, 560);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };

        var grid = Ui.Form();
        grid.Row(null, _grid, height: 400);
        grid.Row(null, Ui.Buttons(Ui.Button("Send test", OnTest)));
        grid.Row(null, _result);
        grid.Fill();

        // Settings on the left, the setup guide for the chosen kind on the right.
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(grid);
        split.Panel2.Controls.Add(_guide);
        split.Panel2.Padding = new Padding(0, 10, 10, 10);
        Load += (_, _) => split.SplitterDistance = Math.Max(420, ClientSize.Width - 420);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        buttons.Controls.AddRange([cancel, ok]);
        Controls.Add(split);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        _guide.ShowGuide(NT.Storix.Core.Destinations.ChannelGuides.For(Channel.Kind));
    }

    private readonly GuidePanel _guide = new() { Dock = DockStyle.Fill };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(520, 0) };

    public NotificationChannel Channel { get; }

    private async void OnTest(object? sender, EventArgs e)
    {
        UseWaitCursor = true;
        try
        {
            var notification = new Notification(NotificationEvent.Test, "Storix test notification", $"This is a test message from Storix on {Environment.MachineName}.");
            await ChannelNotifier.SendAsync(SharedHttp.Client, Channel, notification, CancellationToken.None);
            _result.ForeColor = Color.ForestGreen;
            _result.Text = "✓ " + Localizer.T("The test notification was sent.");
        }
        catch (Exception ex)
        {
            _result.ForeColor = Color.Firebrick;
            _result.Text = "✗ " + Localizer.T("Sending failed:") + " " + ex.Message;
        }
        finally
        {
            UseWaitCursor = false;
        }
    }
}
