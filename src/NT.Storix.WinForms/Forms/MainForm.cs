using System.Diagnostics;
using System.ServiceProcess;
using NT.Storix.Core;
using NT.Storix.Core.Configuration;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Ipc;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Security;
using NT.Storix.Core.Scheduling;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

internal sealed class MainForm : Form
{
    private readonly AppServices _services;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _jobs = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill };
    private readonly ListView _history = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill };
    private readonly TextBox _runLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly ComboBox _historyFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _historyStatus = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly ToolStripStatusLabel _serviceStatus = new();
    private readonly Label _serviceStatusLabel = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 12, FontStyle.Bold) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };

    // Settings
    private readonly NumericUpDown _maxConcurrent = Ui.Number(1, 16);
    private readonly TextBox _staging = new();
    private readonly NumericUpDown _historyDays = Ui.Number(0, 36_500);
    private readonly CheckBox _requireConfirmation = new() { Text = "Ask for my Windows password before restores, deletions, exports with secrets and service changes", AutoSize = true };
    private readonly CheckBox _pauseMetered = new() { Text = "Hold uploads while the internet connection is metered", AutoSize = true };
    private readonly CheckBox _metricsEnabled = new() { Text = "Expose Prometheus metrics (/metrics)", AutoSize = true };
    private readonly NumericUpDown _metricsPort = new() { Minimum = 1, Maximum = 65_535, Width = 100 };
    private readonly CheckBox _metricsRemote = new() { Text = "Allow access from other computers (needs a firewall rule)", AutoSize = true };
    private readonly TextBox _otlpEndpoint = new() { PlaceholderText = "http://otel-collector:4317" };
    private readonly CheckBox _checkUpdates = new() { Text = "Check for new versions on GitHub once a day", AutoSize = true };
    private readonly CheckBox _prereleaseUpdates = new() { Text = "Include pre-release versions", AutoSize = true };
    private readonly ComboBox _uiLanguage = Ui.EnumCombo(UiLanguage.Auto);
    private readonly ComboBox _uiTheme = Ui.EnumCombo(UiTheme.System);
    private readonly ToolStripStatusLabel _updateStatus = new() { IsLink = true, Visible = false };
    private Core.Updates.ReleaseInfo? _availableUpdate;
    private readonly CheckBox _smtpEnabled = new() { Text = "Enable e-mail notifications", AutoSize = true };
    private readonly TextBox _smtpHost = new();
    private readonly NumericUpDown _smtpPort = Ui.Number(1, 65_535, 587);
    private readonly CheckBox _smtpSsl = new() { Text = "Use SSL/TLS", AutoSize = true };
    private readonly TextBox _smtpUser = new();
    private readonly TextBox _smtpPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _smtpFrom = new();
    private readonly CheckBox _summaryEnabled = new() { Text = "Send a weekly summary", AutoSize = true };
    private readonly TextBox _summaryRecipients = new();
    private readonly ComboBox _summaryDay = Ui.EnumCombo(DayOfWeek.Monday);
    private readonly NumericUpDown _summaryHour = Ui.Number(0, 23, 8);
    private readonly ListView _channels = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    private List<NotificationChannel> _channelList = [];

    private List<BackupJob> _jobList = [];
    private readonly TabPage _jobsPage;
    private readonly TabPage _dashboardPage;
    private readonly TabPage _historyPage;
    private readonly TabPage _auditPage;
    private readonly TabPage _servicePage;
    private readonly TabPage _settingsPage;
    private readonly ToolStripStatusLabel _notice = new() { Spring = false };
    private readonly System.Windows.Forms.Timer _noticeTimer = new() { Interval = 8000 };
    private readonly TrayIcon _tray;
    private readonly bool _startInTray;

    public MainForm(AppServices services, bool startInTray = false)
    {
        Localizer.Attach(this);
        _services = services;
        _startInTray = startInTray;
        _tray = new TrayIcon(this, services);

        Text = "Storix - Backup Agent";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 680);
        MinimumSize = new Size(820, 520);

        // Everyday tabs first, administration last.
        _jobsPage = CreatePage("Jobs", BuildJobsTab());
        _dashboardPage = CreatePage("Dashboard", BuildDashboardTab());
        _historyPage = CreatePage("History", BuildHistoryTab());
        _auditPage = CreatePage("Audit", BuildAuditTab());
        _servicePage = CreatePage("Service", BuildServiceTab());
        _settingsPage = CreatePage("Settings", BuildSettingsTab());
        _tabs.TabPages.AddRange([_jobsPage, _dashboardPage, _historyPage, _auditPage, _servicePage, _settingsPage]);
        _tabs.SelectedIndexChanged += (_, _) => RefreshCurrentTab();
        KeyPreview = true;

        var status = new StatusStrip();
        status.Items.Add(new ToolStripStatusLabel($"Data: {StorixPaths.DataDirectory}") { Spring = true, TextAlign = ContentAlignment.MiddleLeft });
        _updateStatus.Click += (_, _) =>
        {
            if (_availableUpdate is not null)
            {
                ShowUpdate(_availableUpdate);
            }
        };
        status.Items.Add(_notice);
        status.Items.Add(_updateStatus);
        status.Items.Add(_serviceStatus);

        Controls.Add(_tabs);
        Controls.Add(BuildMenu());
        Controls.Add(status);

        _timer.Tick += async (_, _) =>
        {
            RefreshTray();
            if (Visible)
            {
                await RefreshRunningAsync();
                RefreshCurrentTab(silent: true);
            }
        };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _notice.Text = string.Empty;
        };
        Load += (_, _) =>
        {
            RestoreLayout();
            RefreshJobs();
            LoadSettings();
            RefreshServiceStatus();
            RefreshTray();
            _timer.Start();
            _ = CheckForUpdatesAsync(manual: false);
        };
        Shown += (_, _) =>
        {
            if (_startInTray)
            {
                BeginInvoke(Hide);
                return;
            }

            ShowWelcomeIfFirstRun();
        };

        // Minimizing hides the window; the tray icon keeps showing the status.
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
            }
        };
        FormClosed += (_, _) => _tray.Dispose();
        FormClosing += (_, _) => SaveLayout();
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            var settings = _services.Settings.Get();
            if (!manual)
            {
                var last = _services.Settings.GetValue("update-check");
                if (!settings.CheckForUpdates
                    || (DateTimeOffset.TryParse(last, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromDays(1)))
                {
                    return;
                }
            }

            _services.Settings.SetValue("update-check", DateTimeOffset.UtcNow.ToString("O"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var release = await Core.Updates.UpdateChecker.CheckAsync(SharedHttp.Client, StorixInfo.Version, settings.IncludePrereleaseUpdates, timeout.Token);
            if (release is null)
            {
                if (manual)
                {
                    Dialogs.Info(this, $"Storix {StorixInfo.Version} is the latest version.");
                }

                return;
            }

            _availableUpdate = release;
            _updateStatus.Text = $"Update available: {release.Version}";
            _updateStatus.Visible = true;
            if (manual)
            {
                ShowUpdate(release);
            }
        }
        catch (Exception ex)
        {
            if (manual)
            {
                Dialogs.Error(this, $"Could not check for updates: {ex.Message}");
            }
        }
    }

    private void ShowUpdate(Core.Updates.ReleaseInfo release)
    {
        using var form = new UpdateForm(release);
        form.ShowDialog(this);
    }

    private void RefreshTray()
    {
        try
        {
            _tray.Refresh();
        }
        catch (Exception)
        {
            // Database busy: try again on the next tick.
        }
    }

    private static TabPage CreatePage(string title, Control content)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true, Name = title };
        page.Controls.Add(content);
        return page;
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Import configuration...", null, (_, _) => ImportConfiguration());
        file.DropDownItems.Add("&Export configuration...", null, (_, _) => ExportConfiguration());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("&Restore backup...", null, (_, _) => OpenRestore());
        tools.DropDownItems.Add("SQL Server &point-in-time restore...", null, (_, _) =>
        {
            using var form = new PointInTimeRestoreForm(_services.SqlBackups, _services.Jobs, _services.Destinations);
            form.ShowDialog(this);
        });
        tools.DropDownItems.Add("&Decrypt backup file...", null, (_, _) =>
        {
            using var form = new DecryptForm();
            form.ShowDialog(this);
        });
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add("Open &data folder", null, (_, _) => OpenFolder(StorixPaths.DataDirectory));
        tools.DropDownItems.Add("Open &logs folder", null, (_, _) => OpenFolder(StorixPaths.LogsDirectory));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("Send &feedback...", null, (_, _) =>
        {
            using var form = new FeedbackForm();
            form.ShowDialog(this);
        });
        help.DropDownItems.Add("Report a &bug", null, (_, _) => Links.Open(this, StorixInfo.NewIssueUrl));
        help.DropDownItems.Add("&GitHub repository", null, (_, _) => Links.Open(this, StorixInfo.RepositoryUrl));
        help.DropDownItems.Add("Check for &updates...", null, async (_, _) => await CheckForUpdatesAsync(manual: true));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add("&About Storix", null, (_, _) =>
        {
            using var form = new AboutForm();
            form.ShowDialog(this);
        });

        menu.Items.AddRange([file, tools, help]);
        return menu;
    }

    // ---------------------------------------------------------------- Jobs

    private readonly ToolStripTextBox _jobSearch = new() { AutoSize = false, Width = 200, ToolTipText = "Search jobs (Ctrl+F)" };
    private readonly Label _jobsSummary = new() { Dock = DockStyle.Top, AutoSize = false, Height = 30, Padding = new Padding(8, 7, 8, 0) };
    private readonly Panel _emptyState = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label _emptyText = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 70 };
    private readonly FlowLayoutPanel _emptyButtons = new() { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false };
    private readonly ContextMenuStrip _jobMenu = new();
    private readonly ContextMenuStrip _templateMenu = new();
    private ToolStripButton _jobEdit = null!;
    private ToolStripButton _jobRun = null!;
    private ToolStripButton _jobPause = null!;
    private ToolStripButton _jobCancel = null!;
    private ToolStripButton _jobRestore = null!;
    private ToolStripDropDownButton _jobMore = null!;
    private Dictionary<Guid, NT.Storix.Core.Ipc.RunningJob> _running = [];

    private static ToolStripButton ToolButton(string text, char glyph, Action action, string? tip = null) =>
        new(text, Glyphs.Icon(glyph), (_, _) => action())
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ToolTipText = tip ?? text,
        };

    private Control BuildJobsTab()
    {
        _jobs.Columns.Add("Name", 220);
        _jobs.Columns.Add("Status", 170);
        _jobs.Columns.Add("Schedule", 190);
        _jobs.Columns.Add("Next run", 130);
        _jobs.Columns.Add("Last run", 130);
        _jobs.Columns.Add("Destinations", 190);
        _jobs.Columns.Add("Source", 100);
        _jobs.SmallImageList = Glyphs.StateImages(LogicalToDeviceUnits(16));
        _jobs.ShowItemToolTips = true;
        _jobs.DoubleClick += (_, _) => EditJob();
        _jobs.SelectedIndexChanged += (_, _) => UpdateJobActions();
        _jobs.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                EditJob();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                DeleteJob();
                e.Handled = true;
            }
        };

        // New: the template gallery; the arrow offers the same three starting points directly.
        _templateMenu.Items.Add(new ToolStripMenuItem("From a template...", Glyphs.Icon(Glyphs.Template), (_, _) => NewFromGallery()) { Font = new Font(Font, FontStyle.Bold) });
        _templateMenu.Items.Add(new ToolStripMenuItem("Set up several backups at once...", Glyphs.Icon(Glyphs.Add), (_, _) => NewBatch())
        {
            ToolTipText = "Websites, databases or folders: one job each, same destination and schedule",
        });
        _templateMenu.Items.Add(new ToolStripMenuItem("Empty job", Glyphs.Icon(Glyphs.Edit), (_, _) => NewJob()));

        var newButton = new ToolStripSplitButton("New", Glyphs.Icon(Glyphs.Add)) { ToolTipText = "New backup job (Ctrl+N)", DropDown = _templateMenu };
        newButton.ButtonClick += (_, _) => NewFromGallery();

        _jobEdit = ToolButton("Edit", Glyphs.Edit, EditJob, "Edit the selected job (Enter)");
        _jobRun = ToolButton("Run now", Glyphs.Play, RunNow, "Start a backup now (Ctrl+R)");
        _jobPause = ToolButton("Pause", Glyphs.Pause, TogglePause);
        _jobCancel = ToolButton("Cancel run", Glyphs.Stop, CancelRun);
        _jobRestore = ToolButton("Restore...", Glyphs.History, OpenRestore, "Restore files from a backup of this job");

        // Less frequent and destructive actions live in "More" (and in the right-click menu).
        _jobMore = new ToolStripDropDownButton("More", Glyphs.Icon(Glyphs.More)) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
        _jobMore.DropDownItems.Add("Duplicate", Glyphs.Icon(Glyphs.Copy), (_, _) => DuplicateJob());
        _jobMore.DropDownItems.Add("Enable / Disable", Glyphs.Icon(Glyphs.Power), (_, _) => ToggleJob());
        _jobMore.DropDownItems.Add("Dry run", Glyphs.Icon(Glyphs.Preview), (_, _) => DryRunJob());
        _jobMore.DropDownItems.Add("Test restore", Glyphs.Icon(Glyphs.Health), (_, _) => RequestDrill());
        _jobMore.DropDownItems.Add(new ToolStripSeparator());
        _jobMore.DropDownItems.Add("Delete", Glyphs.Icon(Glyphs.Delete, color: Color.Firebrick), (_, _) => DeleteJob());

        _jobSearch.TextChanged += (_, _) => RefreshJobs();
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), ImageScalingSize = new Size(16, 16) };
        toolbar.Items.AddRange([newButton, _jobEdit, new ToolStripSeparator(), _jobRun, _jobPause, _jobCancel, new ToolStripSeparator(), _jobRestore, _jobMore]);
        toolbar.Items.Add(new ToolStripButton(null, Glyphs.Icon(Glyphs.Refresh), (_, _) => RefreshJobs()) { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Refresh (F5)", DisplayStyle = ToolStripItemDisplayStyle.Image, Text = "Refresh" });
        toolbar.Items.Add(new ToolStripControlHost(new Panel { Width = 4 }) { Alignment = ToolStripItemAlignment.Right });
        _jobSearch.Alignment = ToolStripItemAlignment.Right;
        _jobSearch.TextBox.PlaceholderText = "Search jobs";
        toolbar.Items.Add(_jobSearch);

        // Right-click menu with the same actions.
        _jobMenu.Items.Add("Edit", Glyphs.Icon(Glyphs.Edit), (_, _) => EditJob());
        _jobMenu.Items.Add("Run now", Glyphs.Icon(Glyphs.Play), (_, _) => RunNow());
        _jobMenu.Items.Add("Pause / Resume", Glyphs.Icon(Glyphs.Pause), (_, _) => TogglePause());
        _jobMenu.Items.Add("Cancel run", Glyphs.Icon(Glyphs.Stop), (_, _) => CancelRun());
        _jobMenu.Items.Add(new ToolStripSeparator());
        _jobMenu.Items.Add("Restore...", Glyphs.Icon(Glyphs.History), (_, _) => OpenRestore());
        _jobMenu.Items.Add("Test restore", Glyphs.Icon(Glyphs.Health), (_, _) => RequestDrill());
        _jobMenu.Items.Add("Dry run", Glyphs.Icon(Glyphs.Preview), (_, _) => DryRunJob());
        _jobMenu.Items.Add("History", Glyphs.Icon(Glyphs.History), (_, _) => ShowHistoryOf(SelectedJob));
        _jobMenu.Items.Add(new ToolStripSeparator());
        _jobMenu.Items.Add("Duplicate", Glyphs.Icon(Glyphs.Copy), (_, _) => DuplicateJob());
        _jobMenu.Items.Add("Enable / Disable", Glyphs.Icon(Glyphs.Power), (_, _) => ToggleJob());
        _jobMenu.Items.Add("Delete", Glyphs.Icon(Glyphs.Delete, color: Color.Firebrick), (_, _) => DeleteJob());
        _jobMenu.Opening += (_, e) => e.Cancel = SelectedJob is null;
        _jobs.ContextMenuStrip = _jobMenu;

        BuildEmptyState();

        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_jobs);
        panel.Controls.Add(_emptyState);
        panel.Controls.Add(_jobsSummary);
        panel.Controls.Add(toolbar);
        UpdateJobActions();
        return panel;
    }

    /// <summary>What to do when there is nothing in the list: create, start from a template or import.</summary>
    private void BuildEmptyState()
    {
        _emptyText.Font = new Font(Font.FontFamily, Font.Size + 3);
        var create = Ui.Button("Create a backup job", (_, _) => NewJob(), 190);
        var several = Ui.Button("Several at once...", (_, _) => NewBatch(), 190);
        var template = Ui.Button("Start from a template...", (_, _) => NewFromGallery(), 190);
        var import = Ui.Button("Import configuration...", (_, _) => ImportConfiguration(), 190);
        _emptyButtons.Controls.AddRange([create, several, template, import]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        _emptyText.Dock = DockStyle.Fill;
        layout.Controls.Add(_emptyText, 0, 1);
        layout.Controls.Add(_emptyButtons, 0, 2);
        _emptyState.Controls.Add(layout);
    }

    private BackupJob? SelectedJob => _jobs.SelectedItems.Count == 0 ? null : (BackupJob)_jobs.SelectedItems[0].Tag!;

    private JobState StateOf(BackupJob job, BackupRun? last) =>
        JobStates.Of(job, last, _running.ContainsKey(job.Id) || last?.Status == RunStatus.Running, _services.Runs.IsPaused(job.Id));

    /// <summary>Enables only the actions that make sense for the selected job (error prevention).</summary>
    private void UpdateJobActions()
    {
        var job = SelectedJob;
        var state = job is null ? (JobState?)null : StateOf(job, _services.Runs.GetLast(job.Id));
        var running = state is JobState.Running or JobState.Paused;
        _jobEdit.Enabled = _jobRestore.Enabled = _jobMore.Enabled = job is not null;
        _jobRun.Enabled = job is not null && !running;
        _jobCancel.Enabled = running;
        _jobPause.Enabled = job is not null;
        _jobPause.Text = Localizer.T(state == JobState.Paused || (job is not null && _services.Runs.IsPaused(job.Id)) ? "Resume" : "Pause");
        _jobPause.Image = Glyphs.Icon(_jobPause.Text == Localizer.T("Resume") ? Glyphs.Play : Glyphs.Pause);
        foreach (ToolStripItem item in _jobMenu.Items)
        {
            if (item.Text == Localizer.T("Cancel run"))
            {
                item.Enabled = running;
            }
            else if (item.Text == Localizer.T("Run now"))
            {
                item.Enabled = !running;
            }
        }
    }

    /// <summary>Running jobs as reported live by the service (local API); empty when it is not reachable.</summary>
    private async Task RefreshRunningAsync()
    {
        var response = await NT.Storix.Core.Ipc.StorixPipe.SendAsync(new NT.Storix.Core.Ipc.PipeRequest { Command = "status" }, TimeSpan.FromSeconds(1));
        _running = response?.Running.ToDictionary(r => r.JobId) ?? [];
    }

    private void RefreshJobs()
    {
        var selectedId = SelectedJob?.Id;
        var topIndex = _jobs.TopItem?.Index ?? 0;
        _jobList = _services.Jobs.GetAll().OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var now = DateTimeOffset.UtcNow;
        var search = _jobSearch.Text.Trim();
        var shown = _jobList.Where(j => search.Length == 0
                                        || j.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                                        || (j.Description?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)
                                        || j.Destinations.Any(d => d.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
            .ToHashSet();

        var states = new Dictionary<Guid, JobState>();
        (BackupJob Job, DateTimeOffset At)? nextRun = null;
        _jobs.BeginUpdate();
        _jobs.Items.Clear();
        foreach (var job in _jobList)
        {
            var last = _services.Runs.GetLast(job.Id);
            var state = StateOf(job, last);
            states[job.Id] = state;
            DateTimeOffset? next = null;
            try
            {
                next = job.Enabled ? ScheduleCalculator.GetNextOccurrence(job.Schedule, now) : null;
            }
            catch
            {
                // Invalid schedule, shown as empty.
            }

            if (next is { } n && (nextRun is null || n < nextRun.Value.At))
            {
                nextRun = (job, n);
            }

            if (!shown.Contains(job))
            {
                continue;
            }

            var status = Localizer.T(JobStates.Describe(state));
            if (_running.TryGetValue(job.Id, out var running))
            {
                status += " · " + TimeText.Elapsed(now - running.StartedAt);
            }
            else if (state == JobState.Running && last is not null)
            {
                status += " · " + TimeText.Elapsed(now - last.StartedAt);
            }

            var item = new ListViewItem(
            [
                job.Name,
                status,
                ScheduleCalculator.Describe(job.Schedule),
                next is null ? "-" : TimeText.Relative(next.Value, now, Localizer.IsPersian),
                last is null ? "-" : TimeText.Relative(last.StartedAt, now, Localizer.IsPersian),
                string.Join(", ", job.Destinations.Where(d => d.Enabled).Select(d => d.Name)),
                job.Source.Kind.ToString(),
            ])
            {
                Tag = job,
                ImageKey = state.ToString(),
                UseItemStyleForSubItems = false,
                ToolTipText = last?.Message ?? job.Description ?? string.Empty,
            };

            item.SubItems[1].ForeColor = Glyphs.StateColor(state);
            if (!job.Enabled)
            {
                item.ForeColor = SystemColors.GrayText;
            }

            item.Selected = job.Id == selectedId;
            _jobs.Items.Add(item);
        }

        _jobs.EndUpdate();

        // Keep the scroll position across the periodic refresh.
        if (_jobs.Items.Count > 0)
        {
            _jobs.TopItem = _jobs.Items[Math.Min(topIndex, _jobs.Items.Count - 1)];
        }

        UpdateJobsSummary(states, nextRun);
        _emptyText.Text = Localizer.T(_jobList.Count == 0
            ? "No backup jobs yet. Create your first one; it only takes a minute."
            : "No job matches the search.");
        _emptyState.Visible = shown.Count == 0;
        _jobs.Visible = shown.Count > 0;
        _emptyButtons.Visible = _jobList.Count == 0;
        UpdateJobActions();
    }

    /// <summary>One line with what matters: how many jobs are fine, failing or running, and what runs next.</summary>
    private void UpdateJobsSummary(Dictionary<Guid, JobState> states, (BackupJob Job, DateTimeOffset At)? next)
    {
        var failed = states.Values.Count(s => s is JobState.Failed or JobState.Warning);
        var running = states.Values.Count(s => s is JobState.Running or JobState.Paused);
        var ok = states.Values.Count(s => s == JobState.Ok);
        var parts = new List<string> { Localizer.F("{0} job(s)", states.Count), Localizer.F("{0} OK", ok) };
        if (failed > 0)
        {
            parts.Add(Localizer.F("{0} need attention", failed));
        }

        if (running > 0)
        {
            parts.Add(Localizer.F("{0} running", running));
        }

        if (next is { } n)
        {
            parts.Add(Localizer.F("next: {0} {1}", n.Job.Name, TimeText.Relative(n.At, DateTimeOffset.UtcNow, Localizer.IsPersian)));
        }

        _jobsSummary.Text = string.Join("   ·   ", parts);
        _jobsSummary.ForeColor = failed > 0 ? Glyphs.StateColor(JobState.Failed) : running > 0 ? Glyphs.StateColor(JobState.Running) : SystemColors.ControlText;
    }

    private void ShowHistoryOf(BackupJob? job)
    {
        _tabs.SelectedTab = _historyPage;
        if (job is not null)
        {
            _historyFilter.SelectedItem = _historyFilter.Items.OfType<JobFilterItem>().FirstOrDefault(i => i.Id == job.Id) ?? _historyFilter.SelectedItem;
        }
    }

    private void ShowWelcomeIfFirstRun()
    {
        if (_jobList.Count > 0)
        {
            return;
        }

        using var welcome = new WelcomeForm(WindowsServiceManager.GetStatus() is not null);
        if (welcome.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (welcome.InstallService)
        {
            ServiceAction(() =>
            {
                WindowsServiceManager.Install();
                WindowsServiceManager.Start();
            }, "installed and started");
        }

        switch (welcome.Choice)
        {
            case WelcomeChoice.Gallery:
                NewFromGallery();
                break;
            case WelcomeChoice.Batch:
                NewBatch();
                break;
            case WelcomeChoice.Blank:
                NewJob();
                break;
            case WelcomeChoice.Import:
                ImportConfiguration();
                break;
        }
    }

    /// <summary>Opens the template gallery and continues with whatever was picked there.</summary>
    private void NewFromGallery()
    {
        using var gallery = new TemplateGalleryForm();
        if (gallery.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        switch (gallery.Choice)
        {
            case GalleryChoice.Template when gallery.Template is { } template:
                NewJob(template.Create());
                break;
            case GalleryChoice.Batch:
                NewBatch();
                break;
            case GalleryChoice.Blank:
                NewJob();
                break;
        }
    }

    /// <summary>Creates one job per website, database or folder with shared destination and schedule.</summary>
    private void NewBatch()
    {
        using var wizard = new BatchSetupForm(_jobList, _services.Destinations);
        if (wizard.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        foreach (var job in wizard.Jobs)
        {
            _services.Jobs.Save(job);
            _services.Audit.Add("job.create", job.Name, AuditDiff.Describe<BackupJob>(null, job));
        }

        RefreshJobs();
        ShowNotice(Localizer.F("Created {0} job(s).", wizard.Jobs.Count));
    }

    private void NewJob(BackupJob? template = null)
    {
        using var editor = new JobEditorForm(template ?? new BackupJob(), _services.Destinations, _jobList);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            _services.Jobs.Save(editor.Job);
            _services.Audit.Add("job.create", editor.Job.Name, AuditDiff.Describe<BackupJob>(null, editor.Job));
            RefreshJobs();
        }
    }

    private void EditJob()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        using var editor = new JobEditorForm(job, _services.Destinations, _jobList);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            _services.Jobs.Save(editor.Job);
            _services.Audit.Add("job.update", editor.Job.Name, AuditDiff.Describe(job, editor.Job));
            RefreshJobs();
        }
    }

    private void DuplicateJob()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        var copy = StorixJson.Clone(job);
        copy.Id = Guid.NewGuid();
        copy.Name = job.Name + " (copy)";
        copy.Enabled = false;
        foreach (var destination in copy.Destinations)
        {
            destination.Id = Guid.NewGuid();
        }

        _services.Jobs.Save(copy);
        _services.Audit.Add("job.duplicate", copy.Name, $"copy of '{job.Name}'");
        RefreshJobs();
    }

    private void DeleteJob()
    {
        if (SelectedJob is { } job && Dialogs.Confirm(this, $"Delete job '{job.Name}'?\n\nBackups already stored on destinations are not deleted.")
            && ConfirmSensitive($"Delete the backup job '{job.Name}'."))
        {
            _services.Jobs.Delete(job.Id);
            _services.Audit.Add("job.delete", job.Name, AuditDiff.Describe<BackupJob>(job, null));
            RefreshJobs();
        }
    }

    private void ToggleJob()
    {
        if (SelectedJob is { } job)
        {
            job.Enabled = !job.Enabled;
            _services.Jobs.Save(job);
            _services.Audit.Add(job.Enabled ? "job.enable" : "job.disable", job.Name);
            ShowNotice(job.Enabled ? $"'{job.Name}' enabled." : $"'{job.Name}' disabled: it no longer runs on schedule.");
            RefreshJobs();
        }
    }

    private async void RunNow()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        var immediate = await ServiceRequests.SendAsync(_services.Runs, "run", job.Id);
        _services.Audit.Add("job.run", job.Name);
        if (immediate)
        {
            ShowNotice($"'{job.Name}' started.");
        }
        else if (WindowsServiceManager.GetStatus() != ServiceControllerStatus.Running)
        {
            ShowNotice("Queued, but the Storix service is not running: start it on the Service tab.", warning: true);
        }
        else
        {
            ShowNotice($"'{job.Name}' was queued and starts within a few seconds.");
        }

        RefreshJobs();
    }

    private async void DryRunJob()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        UseWaitCursor = true;
        try
        {
            var report = await Task.Run(() => NT.Storix.Core.Engine.DryRun.RunAsync(job, _services.Destinations, CancellationToken.None));
            using var form = new Form { Text = $"Dry run - {job.Name}", ClientSize = new Size(720, 520), StartPosition = FormStartPosition.CenterParent };
            form.Controls.Add(new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9),
                Text = report.ReplaceLineEndings(Environment.NewLine),
            });
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async void RequestDrill()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        await ServiceRequests.SendAsync(_services.Runs, "drill", job.Id);
        _services.Audit.Add("job.drill", job.Name);
        if (WindowsServiceManager.GetStatus() == ServiceControllerStatus.Running)
        {
            ShowNotice($"Restore drill of '{job.Name}' started; the result appears in History.");
        }
        else
        {
            ShowNotice("Queued, but the Storix service is not running: start it on the Service tab.", warning: true);
        }
    }

    private void TogglePause()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        var paused = _services.Runs.IsPaused(job.Id);
        _services.Runs.SetPaused(job.Id, !paused);
        _services.Audit.Add(paused ? "job.resume" : "job.pause", job.Name);
        ShowNotice(paused ? $"'{job.Name}' resumed." : $"'{job.Name}' paused: a running backup waits at its next step until you resume it.");
        RefreshJobs();
    }

    private async void CancelRun()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        if (_services.Runs.GetLast(job.Id)?.Status != RunStatus.Running)
        {
            ShowNotice($"'{job.Name}' is not running.", warning: true);
            return;
        }

        if (Dialogs.Confirm(this, $"Cancel the running backup of '{job.Name}'?"))
        {
            var immediate = await ServiceRequests.SendAsync(_services.Runs, "cancel", job.Id);
            _services.Audit.Add("job.cancel", job.Name);
            ShowNotice(immediate ? "Cancellation sent: the backup stops at its next step." : "Cancellation requested: the service stops the backup within a few seconds.");
        }
    }

    private void OpenRestore()
    {
        using var form = new RestoreForm(_services.Jobs.GetAll(), _services.Destinations, SelectedJob, _services.Audit, ConfirmSensitive);
        form.ShowDialog(this);
    }

    // ---------------------------------------------------------------- History

    private Control BuildHistoryTab()
    {
        _history.Columns.Add("Started", 135);
        _history.Columns.Add("Job", 170);
        _history.Columns.Add("Trigger", 75);
        _history.Columns.Add("Status", 120);
        _history.Columns.Add("Duration", 75);
        _history.Columns.Add("Size", 85);
        _history.Columns.Add("File", 230);
        _history.Columns.Add("Message", 400);
        _history.SelectedIndexChanged += (_, _) => ShowRunLog();
        _history.SmallImageList = Glyphs.StateImages(LogicalToDeviceUnits(16));
        _historyFilter.SelectedIndexChanged += (_, _) => RefreshHistory();
        _historyStatus.Items.AddRange(["All runs", "Failures and warnings", "Successful"]);
        _historyStatus.Format += (_, e) => e.Value = Localizer.T((string)e.ListItem!);
        _historyStatus.SelectedIndex = 0;
        _historyStatus.SelectedIndexChanged += (_, _) => RefreshHistory();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4) };
        top.Controls.Add(new Label { Text = "Job:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        top.Controls.Add(_historyFilter);
        top.Controls.Add(new Label { Text = "Show:", AutoSize = true, Margin = new Padding(12, 8, 3, 3) });
        top.Controls.Add(_historyStatus);
        top.Controls.Add(Ui.Button("Refresh", (_, _) => RefreshHistory()));

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
        split.Panel1.Controls.Add(_history);
        split.Panel2.Controls.Add(_runLog);

        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(split);
        panel.Controls.Add(top);
        return panel;
    }

    private void RefreshHistory()
    {
        // Rebuild the filter when the job list changes.
        var filterItems = new List<object> { "(All jobs)" };
        filterItems.AddRange(_jobList.Select(j => new JobFilterItem(j.Id, j.Name)));
        if (_historyFilter.Items.Count != filterItems.Count)
        {
            var previous = _historyFilter.SelectedItem;
            _historyFilter.Items.Clear();
            _historyFilter.Items.AddRange(filterItems.ToArray());
            _historyFilter.SelectedItem = previous is not null && _historyFilter.Items.Contains(previous) ? previous : filterItems[0];
            return; // SelectedIndexChanged triggers the refresh.
        }

        var jobId = (_historyFilter.SelectedItem as JobFilterItem)?.Id;
        var selectedId = _history.SelectedItems.Count > 0 ? ((BackupRun)_history.SelectedItems[0].Tag!).Id : (Guid?)null;

        _history.BeginUpdate();
        _history.Items.Clear();
        foreach (var run in _services.Runs.GetRecent(jobId, 500))
        {
            var failure = run.Status is RunStatus.Failed or RunStatus.Interrupted or RunStatus.PartiallySucceeded or RunStatus.Cancelled;
            if ((_historyStatus.SelectedIndex == 1 && !failure) || (_historyStatus.SelectedIndex == 2 && run.Status != RunStatus.Succeeded))
            {
                continue;
            }

            var item = new ListViewItem(
            [
                run.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                run.JobName,
                run.Trigger.ToString(),
                run.Status.ToString(),
                run.Duration?.ToString(@"hh\:mm\:ss") ?? "-",
                run.SizeBytes is { } size ? NT.Storix.Core.Engine.BackupJobRunner.FormatSize(size) : "-",
                run.FileName ?? "-",
                run.Message ?? string.Empty,
            ])
            {
                Tag = run,
                UseItemStyleForSubItems = false,
                Selected = run.Id == selectedId,
                ImageKey = (run.Status switch
                {
                    RunStatus.Succeeded => JobState.Ok,
                    RunStatus.Running => JobState.Running,
                    RunStatus.PartiallySucceeded or RunStatus.Cancelled => JobState.Warning,
                    _ => JobState.Failed,
                }).ToString(),
            };
            item.SubItems[3].ForeColor = StatusColor(run.Status);
            _history.Items.Add(item);
        }

        _history.EndUpdate();
    }

    private void ShowRunLog()
    {
        if (_history.SelectedItems.Count == 0)
        {
            _runLog.Clear();
            return;
        }

        var run = (BackupRun)_history.SelectedItems[0].Tag!;
        var log = _services.Runs.GetLog(run.Id);
        _runLog.Text = string.IsNullOrEmpty(log)
            ? run.Status == RunStatus.Running ? "The run is in progress..." : "(no log)"
            : log.ReplaceLineEndings(Environment.NewLine) + (run.Sha256 is null ? string.Empty : $"{Environment.NewLine}SHA-256: {run.Sha256}");
    }

    private sealed record JobFilterItem(Guid Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Windows password confirmation for sensitive actions, when enabled in the settings.</summary>
    private bool ConfirmSensitive(string reason)
    {
        if (!_services.Settings.Get().RequireWindowsConfirmation)
        {
            return true;
        }

        if (WindowsConfirmation.Verify(this, reason))
        {
            return true;
        }

        _services.Audit.Add("confirmation.failed", reason);
        Dialogs.Error(this, "The action was not confirmed.");
        return false;
    }

    // ---------------------------------------------------------------- Audit

    private readonly ListView _audit = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };

    private Control BuildAuditTab()
    {
        _audit.Columns.Add("When", 140);
        _audit.Columns.Add("User", 170);
        _audit.Columns.Add("Action", 120);
        _audit.Columns.Add("Target", 180);
        _audit.Columns.Add("Details", 600);
        _audit.DoubleClick += (_, _) =>
        {
            if (_audit.SelectedItems.Count > 0)
            {
                Dialogs.Info(this, (string)_audit.SelectedItems[0].Tag!);
            }
        };
        return _audit;
    }

    private void RefreshAudit()
    {
        _audit.BeginUpdate();
        _audit.Items.Clear();
        foreach (var entry in _services.Audit.GetRecent())
        {
            _audit.Items.Add(new ListViewItem([entry.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), $"{entry.User} ({entry.Machine})", entry.Action, entry.Target, entry.Details ?? string.Empty])
            {
                Tag = $"{entry.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {entry.User} on {entry.Machine}\n{entry.Action}: {entry.Target}\n\n{entry.Details?.Replace("; ", "\n")}",
            });
        }

        _audit.EndUpdate();
    }

    // ---------------------------------------------------------------- Dashboard

    private readonly Label _dashboardSummary = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 11, FontStyle.Bold), Padding = new Padding(4, 8, 4, 8) };
    private readonly ListView _dashboardJobs = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };
    private readonly ListView _dashboardDestinations = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };

    private Control BuildDashboardTab()
    {
        _dashboardJobs.Columns.Add("Job", 200);
        _dashboardJobs.Columns.Add("Success (30 days)", 120);
        _dashboardJobs.Columns.Add("Runs", 60);
        _dashboardJobs.Columns.Add("Failed", 60);
        _dashboardJobs.Columns.Add("Latest size", 100);
        _dashboardJobs.Columns.Add("Stored (est.)", 100);
        _dashboardJobs.Columns.Add("Growth / day", 100);
        _dashboardJobs.Columns.Add("Size in 30 days", 110);
        _dashboardDestinations.Columns.Add("Destination", 200);
        _dashboardDestinations.Columns.Add("Jobs", 60);
        _dashboardDestinations.Columns.Add("Stored (est.)", 120);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
        split.Panel1.Controls.Add(_dashboardJobs);
        split.Panel2.Controls.Add(_dashboardDestinations);

        var note = new Label
        {
            Text = "Estimated from the run history and each job's retention policy; destinations are not contacted.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Bottom,
            Padding = new Padding(4),
        };
        _dashboardSummary.Dock = DockStyle.Top;

        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(split);
        panel.Controls.Add(note);
        panel.Controls.Add(_dashboardSummary);
        return panel;
    }

    private void RefreshDashboard()
    {
        var dashboard = DashboardStats.Compute(_services.Jobs.GetAll(), _services.Runs.GetRecent(null, 20_000), DateTimeOffset.UtcNow);
        _dashboardSummary.Text = $"{dashboard.Jobs.Count} job(s)   •   success rate (30 days): {Percent(dashboard.SuccessRate)}   •   stored: {BackupJobRunner.FormatSize(dashboard.TotalStored)}";

        _dashboardJobs.BeginUpdate();
        _dashboardJobs.Items.Clear();
        foreach (var stats in dashboard.Jobs.OrderBy(j => j.Job.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ListViewItem(
            [
                stats.Job.Name,
                Percent(stats.SuccessRate),
                stats.Runs.ToString(),
                stats.Failed.ToString(),
                stats.LatestSize is { } size ? BackupJobRunner.FormatSize(size) : "-",
                BackupJobRunner.FormatSize(stats.EstimatedStored),
                stats.GrowthPerDay is { } growth ? (growth < 0 ? "-" : "+") + BackupJobRunner.FormatSize((long)Math.Abs(growth)) : "-",
                stats.ForecastSize is { } forecast ? BackupJobRunner.FormatSize(forecast) : "-",
            ]);
            if (stats.Failed > 0)
            {
                item.ForeColor = Color.Firebrick;
            }

            _dashboardJobs.Items.Add(item);
        }

        _dashboardJobs.EndUpdate();

        _dashboardDestinations.BeginUpdate();
        _dashboardDestinations.Items.Clear();
        foreach (var destination in dashboard.Destinations)
        {
            _dashboardDestinations.Items.Add(new ListViewItem([destination.Destination, destination.Jobs.ToString(), BackupJobRunner.FormatSize(destination.EstimatedStored)]));
        }

        _dashboardDestinations.EndUpdate();
    }

    private static string Percent(double? rate) => rate is { } value ? $"{value:P0}" : "-";

    // ---------------------------------------------------------------- Settings

    private Control BuildSettingsTab()
    {
        var grid = Ui.Form();
        grid.Row(null, new Label { Text = "Engine", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        grid.Row("Max concurrent jobs", _maxConcurrent);
        grid.Row("Staging folder (empty = default)", _staging);
        grid.Row("Keep history (days, 0 = forever)", _historyDays);
        grid.Row(null, _pauseMetered);
        grid.Row(null, _requireConfirmation);
        grid.Row(null, new Label { Text = "SMTP (e-mail notifications)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        grid.Row(null, _smtpEnabled);
        grid.Row("Host", _smtpHost);
        grid.Row("Port", _smtpPort);
        grid.Row(null, _smtpSsl);
        grid.Row("User name", _smtpUser);
        grid.Row("Password", _smtpPassword);
        grid.Row("From address", _smtpFrom);
        grid.Row(null, Ui.Buttons(Ui.Button("Send test e-mail...", (_, _) => SendTestEmail(), 150)));
        grid.Row(null, _summaryEnabled);
        grid.Row("Summary recipients", _summaryRecipients);
        grid.Row("Summary day", _summaryDay);
        grid.Row("Summary hour", _summaryHour);
        grid.Row(null, new Label { Text = "Chat and webhook channels (receive notifications of every job)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _channels.Columns.Add("Name", 200);
        _channels.Columns.Add("Type", 100);
        _channels.Columns.Add("Enabled", 70);
        _channels.Columns.Add("Only failures", 100);
        _channels.DoubleClick += (_, _) => EditChannel();
        grid.Row(null, _channels, height: 120);
        grid.Row(null, Ui.Buttons(
            Ui.Button("Add...", (_, _) => AddChannel()),
            Ui.Button("Edit...", (_, _) => EditChannel()),
            Ui.Button("Remove", (_, _) => RemoveChannel())));
        grid.Row(null, new Label { Text = "Monitoring", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        grid.Row(null, _metricsEnabled);
        grid.Row("Metrics port", _metricsPort);
        grid.Row(null, _metricsRemote);
        grid.Row("OpenTelemetry endpoint (OTLP)", _otlpEndpoint);
        grid.Row(null, new Label { Text = "Updates", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        grid.Row(null, _checkUpdates);
        grid.Row(null, _prereleaseUpdates);
        grid.Row(null, new Label { Text = "Appearance", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        grid.Row("Language", _uiLanguage);
        grid.Row("Theme", _uiTheme);
        grid.Row(null, new Label { Text = "Restart Storix Manager to apply language and theme changes.", AutoSize = true, ForeColor = SystemColors.GrayText });
        grid.Row(null, Ui.Buttons(Ui.Button("Save settings", (_, _) => SaveSettings(), 130)));
        grid.Row(null, new Label { Text = "Restart the service to apply engine changes.", AutoSize = true, ForeColor = SystemColors.GrayText });
        grid.Fill();
        return grid;
    }

    private void LoadSettings()
    {
        var settings = _services.Settings.Get();
        _maxConcurrent.Value = Math.Clamp(settings.MaxConcurrentJobs, 1, 16);
        _staging.Text = settings.StagingDirectory;
        _historyDays.Value = Math.Clamp(settings.HistoryRetentionDays, 0, 36_500);
        _pauseMetered.Checked = settings.PauseOnMeteredConnection;
        _requireConfirmation.Checked = settings.RequireWindowsConfirmation;
        _smtpEnabled.Checked = settings.Smtp.Enabled;
        _smtpHost.Text = settings.Smtp.Host;
        _smtpPort.Value = Math.Clamp(settings.Smtp.Port, 1, 65_535);
        _smtpSsl.Checked = settings.Smtp.UseSsl;
        _smtpUser.Text = settings.Smtp.UserName;
        _smtpPassword.Text = settings.Smtp.Password;
        _smtpFrom.Text = settings.Smtp.From;
        _channelList = settings.Channels;
        _summaryEnabled.Checked = settings.WeeklySummary.Enabled;
        _summaryRecipients.Text = settings.WeeklySummary.Recipients;
        _summaryDay.SelectedItem = settings.WeeklySummary.Day;
        _summaryHour.Value = Math.Clamp(settings.WeeklySummary.Hour, 0, 23);
        _metricsEnabled.Checked = settings.Observability.MetricsEnabled;
        _metricsPort.Value = Math.Clamp(settings.Observability.MetricsPort, 1, 65_535);
        _metricsRemote.Checked = settings.Observability.MetricsRemoteAccess;
        _otlpEndpoint.Text = settings.Observability.OtlpEndpoint;
        _checkUpdates.Checked = settings.CheckForUpdates;
        _prereleaseUpdates.Checked = settings.IncludePrereleaseUpdates;
        var preferences = UiPreferences.Load();
        _uiLanguage.SelectedItem = preferences.Language;
        _uiTheme.SelectedItem = preferences.Theme;
        RefreshChannels();
    }

    private void RefreshChannels()
    {
        _channels.Items.Clear();
        foreach (var channel in _channelList)
        {
            _channels.Items.Add(new ListViewItem([channel.Name, channel.Kind.ToString(), channel.Enabled ? "Yes" : "No", channel.OnlyFailures ? "Yes" : "No"]) { Tag = channel });
        }
    }

    private void AddChannel()
    {
        using var editor = new ChannelEditorForm(new NotificationChannel { Name = $"Channel {_channelList.Count + 1}" });
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            _channelList.Add(editor.Channel);
            RefreshChannels();
        }
    }

    private void EditChannel()
    {
        if (_channels.SelectedItems.Count == 0)
        {
            return;
        }

        var current = (NotificationChannel)_channels.SelectedItems[0].Tag!;
        using var editor = new ChannelEditorForm(current);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            _channelList[_channelList.IndexOf(current)] = editor.Channel;
            RefreshChannels();
        }
    }

    private void RemoveChannel()
    {
        if (_channels.SelectedItems.Count > 0 && Dialogs.Confirm(this, "Remove this channel?"))
        {
            _channelList.Remove((NotificationChannel)_channels.SelectedItems[0].Tag!);
            RefreshChannels();
        }
    }

    private async void SendTestEmail()
    {
        var smtp = ReadSmtp();
        if (string.IsNullOrWhiteSpace(smtp.Host) || string.IsNullOrWhiteSpace(smtp.From))
        {
            Dialogs.Error(this, "Enter the SMTP host and the From address first.");
            return;
        }

        UseWaitCursor = true;
        try
        {
            await EmailNotifier.SendAsync(smtp, smtp.From, "[Storix] Test e-mail", $"This is a test e-mail from Storix on {Environment.MachineName}.", CancellationToken.None);
            Dialogs.Info(this, $"A test e-mail was sent to {smtp.From}.");
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

    private SmtpSettings ReadSmtp() => new()
    {
        Enabled = _smtpEnabled.Checked,
        Host = _smtpHost.Text.Trim(),
        Port = (int)_smtpPort.Value,
        UseSsl = _smtpSsl.Checked,
        UserName = string.IsNullOrWhiteSpace(_smtpUser.Text) ? null : _smtpUser.Text.Trim(),
        Password = string.IsNullOrEmpty(_smtpPassword.Text) ? null : _smtpPassword.Text,
        From = _smtpFrom.Text.Trim(),
    };

    private void SaveSettings()
    {
        var settings = _services.Settings.Get();
        settings.MaxConcurrentJobs = (int)_maxConcurrent.Value;
        settings.StagingDirectory = string.IsNullOrWhiteSpace(_staging.Text) ? null : _staging.Text.Trim();
        settings.HistoryRetentionDays = (int)_historyDays.Value;
        settings.PauseOnMeteredConnection = _pauseMetered.Checked;
        if (settings.RequireWindowsConfirmation && !_requireConfirmation.Checked && !ConfirmSensitive("Turn off the Windows confirmation for sensitive actions."))
        {
            return;
        }

        settings.RequireWindowsConfirmation = _requireConfirmation.Checked;
        settings.Smtp = ReadSmtp();
        settings.Channels = _channelList;
        settings.WeeklySummary = new WeeklySummarySettings
        {
            Enabled = _summaryEnabled.Checked,
            Recipients = string.IsNullOrWhiteSpace(_summaryRecipients.Text) ? null : _summaryRecipients.Text.Trim(),
            Day = (DayOfWeek)_summaryDay.SelectedItem!,
            Hour = (int)_summaryHour.Value,
        };
        settings.CheckForUpdates = _checkUpdates.Checked;
        settings.IncludePrereleaseUpdates = _prereleaseUpdates.Checked;
        var preferences = UiPreferences.Load();
        preferences.Language = (UiLanguage)_uiLanguage.SelectedItem!;
        preferences.Theme = (UiTheme)_uiTheme.SelectedItem!;
        preferences.Save();
        settings.Observability = new ObservabilitySettings
        {
            MetricsEnabled = _metricsEnabled.Checked,
            MetricsPort = (int)_metricsPort.Value,
            MetricsRemoteAccess = _metricsRemote.Checked,
            OtlpEndpoint = string.IsNullOrWhiteSpace(_otlpEndpoint.Text) ? null : _otlpEndpoint.Text.Trim(),
        };

        _services.Audit.Add("settings.update", "settings", AuditDiff.Describe(_services.Settings.Get(), settings));
        _services.Settings.Save(settings);
        ShowNotice("Settings saved.");
    }

    // ---------------------------------------------------------------- Service

    private Control BuildServiceTab()
    {
        var grid = Ui.Form();
        grid.Row(null, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = "Backups are executed by the \"Storix\" Windows service, so they run even when nobody is logged on. " +
                   "This manager only edits the configuration stored in the shared database.",
        });
        grid.Row("Status", _serviceStatusLabel);
        grid.Row(null, Ui.Buttons(
            Ui.Button("Install", (_, _) => ServiceAction(WindowsServiceManager.Install, "installed")),
            Ui.Button("Uninstall", (_, _) =>
            {
                if (Dialogs.Confirm(this, "Uninstall the Storix service? Scheduled backups will stop.") && ConfirmSensitive("Uninstall the Storix service."))
                {
                    ServiceAction(WindowsServiceManager.Uninstall, "uninstalled");
                }
            }),
            Ui.Button("Start", (_, _) => ServiceAction(WindowsServiceManager.Start, "started")),
            Ui.Button("Stop", (_, _) => ServiceAction(WindowsServiceManager.Stop, "stopped")),
            Ui.Button("Restart", (_, _) => ServiceAction(() =>
            {
                WindowsServiceManager.Stop();
                WindowsServiceManager.Start();
            }, "restarted"))));
        grid.Row(null, Ui.Buttons(Ui.Button("Run as account...", (_, _) =>
        {
            if (WindowsServiceManager.GetStatus() is null)
            {
                Dialogs.Error(this, "Install the service first.");
                return;
            }

            if (ConfirmSensitive("Change the account the Storix service runs as."))
            {
                using var form = new ServiceAccountForm();
                form.ShowDialog(this);
                _services.Audit.Add("service.account", StorixPaths.ServiceName, ServiceAccount.Current());
            }
        }, 150)));
        grid.Fill();
        return grid;
    }

    private async void ServiceAction(Action action, string verb)
    {
        UseWaitCursor = true;
        try
        {
            await Task.Run(action);
            _services.Audit.Add("service." + verb.Split(' ')[0], StorixPaths.ServiceName);
            ShowNotice($"The service was {verb}.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
        finally
        {
            UseWaitCursor = false;
            RefreshServiceStatus();
        }
    }

    private void RefreshServiceStatus()
    {
        var status = WindowsServiceManager.GetStatus();
        var text = WindowsServiceManager.DescribeStatus();
        _serviceStatus.Text = $"Service: {text}";
        _serviceStatusLabel.Text = text;
        _serviceStatusLabel.ForeColor = status == ServiceControllerStatus.Running ? Color.ForestGreen : Color.Firebrick;
    }

    // ---------------------------------------------------------------- Import / export

    private void ExportConfiguration()
    {
        var includeSecrets = MessageBox.Show(this,
            "Include secrets (passwords, connection strings, encryption keys)?\n\n" +
            "Yes: secrets are included and protected with a passphrase you choose.\n" +
            "No: secrets are removed; you will re-enter them after importing.",
            "Export configuration", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (includeSecrets == DialogResult.Cancel)
        {
            return;
        }

        string? passphrase = null;
        if (includeSecrets == DialogResult.Yes)
        {
            using var dialog = new PassphraseDialog("Export passphrase", "Choose a passphrase to protect the secrets in the exported file.", requireConfirmation: true);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            passphrase = dialog.Passphrase;
            if (!ConfirmSensitive("Export the configuration including passwords and keys."))
            {
                return;
            }
        }

        using var save = new SaveFileDialog
        {
            Filter = "Storix configuration (*.storix.json)|*.storix.json|JSON (*.json)|*.json",
            FileName = $"storix-config-{Environment.MachineName.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd}.storix.json",
        };
        if (save.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var json = ConfigurationPorter.Export(_services.Jobs.GetAll(), _services.Settings.Get(), passphrase);
            File.WriteAllText(save.FileName, json);
            _services.Audit.Add("config.export", save.FileName, passphrase is null ? "without secrets" : "with passphrase-protected secrets");
            ShowNotice($"Exported {_jobList.Count} job(s).");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
    }

    private void ImportConfiguration()
    {
        using var open = new OpenFileDialog { Filter = "Storix configuration (*.storix.json;*.json)|*.storix.json;*.json|All files (*.*)|*.*" };
        if (open.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(open.FileName);
            string? passphrase = null;
            if (ConfigurationPorter.RequiresPassphrase(json))
            {
                using var dialog = new PassphraseDialog("Import passphrase", "This file contains protected secrets. Enter the passphrase used when exporting.", requireConfirmation: false);
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                passphrase = dialog.Passphrase;
            }

            var package = ConfigurationPorter.Import(json, passphrase);
            var existing = _services.Jobs.GetAll().Select(j => j.Id).ToHashSet();
            var replaced = package.Jobs.Count(j => existing.Contains(j.Id));

            if (!Dialogs.Confirm(this, $"Import {package.Jobs.Count} job(s)?" + (replaced > 0 ? $"\n\n{replaced} existing job(s) with the same id will be replaced." : string.Empty)))
            {
                return;
            }

            foreach (var job in package.Jobs)
            {
                _services.Audit.Add("config.import", job.Name, AuditDiff.Describe(_services.Jobs.Get(job.Id), job));
                _services.Jobs.Save(job);
            }

            if (package.Settings is not null && Dialogs.Confirm(this, "The file also contains global settings (concurrency, staging folder, SMTP). Import them as well?"))
            {
                _services.Settings.Save(package.Settings);
                LoadSettings();
            }

            RefreshJobs();
            Dialogs.Info(this, package.Secrets is null && passphrase is null
                ? "Import completed. Secrets were not included in the file: edit the jobs to enter passwords and connection strings."
                : "Import completed.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
    }

    // ---------------------------------------------------------------- Helpers

    private void RefreshCurrentTab(bool silent = false)
    {
        try
        {
            RefreshServiceStatus();
            var page = _tabs.SelectedTab;
            if (page == _jobsPage)
            {
                // Keep the selection stable while the user is working in the list.
                if (!silent || !_jobs.Focused || _jobs.SelectedItems.Count == 0)
                {
                    RefreshJobs();
                }
            }
            else if (page == _historyPage)
            {
                RefreshHistory();
            }
            else if (page == _auditPage && !silent)
            {
                RefreshAudit();
            }
            else if (page == _dashboardPage)
            {
                RefreshDashboard();
            }
        }
        catch (Exception) when (silent)
        {
            // Database busy: try again on the next tick.
        }
    }

    private IEnumerable<(string Name, ListView List)> Lists =>
        [("jobs", _jobs), ("history", _history), ("audit", _audit), ("dashboardJobs", _dashboardJobs), ("dashboardDestinations", _dashboardDestinations)];

    /// <summary>Restores the window, tab and column widths of the last session (per Windows user).</summary>
    private void RestoreLayout()
    {
        var preferences = UiPreferences.Load();
        if (preferences.WindowBounds is [var x, var y, var width, var height])
        {
            var bounds = new Rectangle(x, y, width, height);
            if (Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
            }
        }

        if (preferences.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }

        if (_tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Name == preferences.Tab) is { } tab)
        {
            _tabs.SelectedTab = tab;
        }

        foreach (var (name, list) in Lists)
        {
            if (preferences.Columns.TryGetValue(name, out var widths) && widths.Length == list.Columns.Count)
            {
                for (var i = 0; i < widths.Length; i++)
                {
                    list.Columns[i].Width = Math.Clamp(widths[i], 30, 2000);
                }
            }
        }
    }

    private void SaveLayout()
    {
        try
        {
            var preferences = UiPreferences.Load();
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            preferences.WindowBounds = [bounds.X, bounds.Y, bounds.Width, bounds.Height];
            preferences.Maximized = WindowState == FormWindowState.Maximized;
            preferences.Tab = _tabs.SelectedTab?.Name;
            foreach (var (name, list) in Lists)
            {
                preferences.Columns[name] = list.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
            }

            preferences.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Layout is a convenience: never block closing.
        }
    }

    /// <summary>A short message in the status bar that disappears by itself (instead of a message box).</summary>
    private void ShowNotice(string message, bool warning = false)
    {
        _notice.Text = (warning ? "⚠ " : "✓ ") + Localizer.T(message);
        _notice.ForeColor = warning ? Glyphs.StateColor(JobState.Warning) : Glyphs.StateColor(JobState.Ok);
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.N:
                NewFromGallery();
                return true;
            case Keys.Control | Keys.R when SelectedJob is not null:
                RunNow();
                return true;
            case Keys.Control | Keys.D when SelectedJob is not null:
                DuplicateJob();
                return true;
            case Keys.Control | Keys.F:
                _tabs.SelectedTab = _jobsPage;
                _jobSearch.Focus();
                return true;
            case Keys.F5:
                RefreshCurrentTab();
                return true;
            case Keys.Escape when _jobSearch.Focused && _jobSearch.Text.Length > 0:
                _jobSearch.Clear();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static Color StatusColor(RunStatus status) => status switch
    {
        RunStatus.Succeeded => Color.ForestGreen,
        RunStatus.PartiallySucceeded => Color.DarkOrange,
        RunStatus.Running => Color.RoyalBlue,
        RunStatus.Cancelled => SystemColors.GrayText,
        _ => Color.Firebrick,
    };

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
    }
}
