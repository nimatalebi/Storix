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
        Channel = StorixJson.Clone(channel);
        Text = "Notification channel";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 480);
        MinimizeBox = false;

        _grid.SelectedObject = Channel;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };

        var grid = Ui.Form();
        grid.Row(null, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Text = "Telegram/Bale: create a bot, add it to your chat and enter the bot token and chat id. " +
                   "Slack/Teams/Discord: paste an incoming webhook URL. Webhook: Storix POSTs a JSON document.",
        });
        grid.Row(null, _grid, height: 330);
        grid.Row(null, Ui.Buttons(Ui.Button("Send test", OnTest), ok, cancel));
        grid.Fill();
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public NotificationChannel Channel { get; }

    private async void OnTest(object? sender, EventArgs e)
    {
        UseWaitCursor = true;
        try
        {
            var notification = new Notification(NotificationEvent.Test, "Storix test notification", $"This is a test message from Storix on {Environment.MachineName}.");
            await ChannelNotifier.SendAsync(SharedHttp.Client, Channel, notification, CancellationToken.None);
            Dialogs.Info(this, "The test notification was sent.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, $"Sending failed:\n\n{ex.Message}");
        }
        finally
        {
            UseWaitCursor = false;
        }
    }
}
