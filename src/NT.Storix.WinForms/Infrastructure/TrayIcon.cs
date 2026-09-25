using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;

namespace NT.Storix.WinForms.Infrastructure;

/// <summary>Notification-area icon with the overall status, "Run now" for each job and failure balloons.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Form _owner;
    private readonly AppServices _services;
    private readonly NotifyIcon _icon = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _runNow = new("&Run now");
    private HashSet<string> _knownFailures = [];
    private bool _initialized;

    public TrayIcon(Form owner, AppServices services)
    {
        _owner = owner;
        _services = services;

        _menu.Items.Add(_status);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("&Open Storix", null, (_, _) => ShowOwner());
        _menu.Items.Add(_runNow);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("E&xit", null, (_, _) => _owner.Close());
        _menu.Opening += (_, _) => RebuildJobMenu();

        _icon.ContextMenuStrip = _menu;
        _icon.Icon = SystemIcons.Shield;
        _icon.Text = "Storix";
        _icon.Visible = true;
        _icon.DoubleClick += (_, _) => ShowOwner();
        _icon.BalloonTipClicked += (_, _) => ShowOwner();
    }

    public void ShowOwner()
    {
        _owner.Show();
        if (_owner.WindowState == FormWindowState.Minimized)
        {
            _owner.WindowState = FormWindowState.Normal;
        }

        _owner.Activate();
    }

    public void Refresh()
    {
        var summary = StatusSummary.Create(_services.Jobs.GetAll(), _services.Runs.GetLast);
        _icon.Text = summary.Text;
        _status.Text = summary.Text;
        _icon.Icon = summary.Status switch
        {
            OverallStatus.Error => SystemIcons.Error,
            OverallStatus.Warning => SystemIcons.Warning,
            OverallStatus.Running => SystemIcons.Information,
            _ => SystemIcons.Shield,
        };

        // Only announce failures that appeared since the last refresh, not the ones present at start-up.
        var failures = summary.Failed.ToHashSet();
        var fresh = failures.Except(_knownFailures).ToList();
        if (_initialized && fresh.Count > 0)
        {
            _icon.ShowBalloonTip(10_000, "Storix backup failed", string.Join(", ", fresh), ToolTipIcon.Error);
        }

        _knownFailures = failures;
        _initialized = true;
    }

    private void RebuildJobMenu()
    {
        _runNow.DropDownItems.Clear();
        foreach (var job in _services.Jobs.GetAll().Where(j => j.Enabled))
        {
            var id = job.Id;
            var name = job.Name;
            _runNow.DropDownItems.Add(name, null, (_, _) =>
            {
                _services.Runs.RequestRun(id);
                _services.Audit.Add("job.run", name);
                _icon.ShowBalloonTip(5_000, "Storix", $"'{name}' was queued.", ToolTipIcon.Info);
            });
        }

        _runNow.Enabled = _runNow.DropDownItems.Count > 0;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
