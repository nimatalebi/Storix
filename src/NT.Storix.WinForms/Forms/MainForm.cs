using System.Diagnostics;
using System.ServiceProcess;
using NT.Storix.Core;
using NT.Storix.Core.Configuration;
using NT.Storix.Core.Models;
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
    private readonly ToolStripStatusLabel _serviceStatus = new();
    private readonly Label _serviceStatusLabel = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 12, FontStyle.Bold) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };

    // Settings
    private readonly NumericUpDown _maxConcurrent = Ui.Number(1, 16);
    private readonly TextBox _staging = new();
    private readonly NumericUpDown _historyDays = Ui.Number(0, 36_500);
    private readonly CheckBox _smtpEnabled = new() { Text = "Enable e-mail notifications", AutoSize = true };
    private readonly TextBox _smtpHost = new();
    private readonly NumericUpDown _smtpPort = Ui.Number(1, 65_535, 587);
    private readonly CheckBox _smtpSsl = new() { Text = "Use SSL/TLS", AutoSize = true };
    private readonly TextBox _smtpUser = new();
    private readonly TextBox _smtpPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _smtpFrom = new();

    private List<BackupJob> _jobList = [];

    public MainForm(AppServices services)
    {
        _services = services;

        Text = "Storix - Backup Agent";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 680);
        MinimumSize = new Size(820, 520);

        _tabs.TabPages.Add(CreatePage("Jobs", BuildJobsTab()));
        _tabs.TabPages.Add(CreatePage("History", BuildHistoryTab()));
        _tabs.TabPages.Add(CreatePage("Settings", BuildSettingsTab()));
        _tabs.TabPages.Add(CreatePage("Service", BuildServiceTab()));
        _tabs.SelectedIndexChanged += (_, _) => RefreshCurrentTab();

        var status = new StatusStrip();
        status.Items.Add(new ToolStripStatusLabel($"Data: {StorixPaths.DataDirectory}") { Spring = true, TextAlign = ContentAlignment.MiddleLeft });
        status.Items.Add(_serviceStatus);

        Controls.Add(_tabs);
        Controls.Add(BuildMenu());
        Controls.Add(status);

        _timer.Tick += (_, _) => RefreshCurrentTab(silent: true);
        Load += (_, _) =>
        {
            RefreshJobs();
            LoadSettings();
            RefreshServiceStatus();
            _timer.Start();
        };
    }

    private static TabPage CreatePage(string title, Control content)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true };
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
        tools.DropDownItems.Add("&Decrypt backup file...", null, (_, _) =>
        {
            using var form = new DecryptForm();
            form.ShowDialog(this);
        });
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add("Open &data folder", null, (_, _) => OpenFolder(StorixPaths.DataDirectory));
        tools.DropDownItems.Add("Open &logs folder", null, (_, _) => OpenFolder(StorixPaths.LogsDirectory));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("&About", null, (_, _) => Dialogs.Info(this,
            $"Storix {typeof(MainForm).Assembly.GetName().Version?.ToString(3)}\n\nOpen-source backup agent for Windows.\nFiles, SQL Server and MongoDB to local folders, FTP, SFTP and Google Drive.\n\nReleased under the MIT License."));

        menu.Items.AddRange([file, tools, help]);
        return menu;
    }

    // ---------------------------------------------------------------- Jobs

    private Control BuildJobsTab()
    {
        _jobs.Columns.Add("Name", 200);
        _jobs.Columns.Add("Enabled", 65);
        _jobs.Columns.Add("Schedule", 190);
        _jobs.Columns.Add("Source", 90);
        _jobs.Columns.Add("Destinations", 180);
        _jobs.Columns.Add("Next run", 130);
        _jobs.Columns.Add("Last run", 130);
        _jobs.Columns.Add("Last status", 130);
        _jobs.DoubleClick += (_, _) => EditJob();

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        toolbar.Items.Add(new ToolStripButton("New", null, (_, _) => NewJob()));
        toolbar.Items.Add(new ToolStripButton("Edit", null, (_, _) => EditJob()));
        toolbar.Items.Add(new ToolStripButton("Duplicate", null, (_, _) => DuplicateJob()));
        toolbar.Items.Add(new ToolStripButton("Delete", null, (_, _) => DeleteJob()));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripButton("Enable / Disable", null, (_, _) => ToggleJob()));
        toolbar.Items.Add(new ToolStripButton("Run now", null, (_, _) => RunNow()));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripButton("Refresh", null, (_, _) => RefreshJobs()));

        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_jobs);
        panel.Controls.Add(toolbar);
        return panel;
    }

    private BackupJob? SelectedJob => _jobs.SelectedItems.Count == 0 ? null : (BackupJob)_jobs.SelectedItems[0].Tag!;

    private void RefreshJobs()
    {
        var selectedId = SelectedJob?.Id;
        _jobList = _services.Jobs.GetAll().ToList();

        _jobs.BeginUpdate();
        _jobs.Items.Clear();
        foreach (var job in _jobList)
        {
            var last = _services.Runs.GetLast(job.Id);
            DateTimeOffset? next = null;
            try
            {
                next = job.Enabled ? ScheduleCalculator.GetNextOccurrence(job.Schedule, DateTimeOffset.UtcNow) : null;
            }
            catch
            {
                // Invalid schedule, shown as empty.
            }

            var item = new ListViewItem(
            [
                job.Name,
                job.Enabled ? "Yes" : "No",
                ScheduleCalculator.Describe(job.Schedule),
                job.Source.Kind.ToString(),
                string.Join(", ", job.Destinations.Where(d => d.Enabled).Select(d => d.Name)),
                next?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-",
                last?.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-",
                last?.Status.ToString() ?? "-",
            ])
            {
                Tag = job,
                ForeColor = job.Enabled ? SystemColors.WindowText : SystemColors.GrayText,
            };

            if (last is not null)
            {
                item.UseItemStyleForSubItems = false;
                item.SubItems[7].ForeColor = StatusColor(last.Status);
            }

            item.Selected = job.Id == selectedId;
            _jobs.Items.Add(item);
        }

        _jobs.EndUpdate();
    }

    private void NewJob()
    {
        using var editor = new JobEditorForm(new BackupJob(), _services.Destinations);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            _services.Jobs.Save(editor.Job);
            RefreshJobs();
        }
    }

    private void EditJob()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        using var editor = new JobEditorForm(job, _services.Destinations);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            _services.Jobs.Save(editor.Job);
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
        RefreshJobs();
    }

    private void DeleteJob()
    {
        if (SelectedJob is { } job && Dialogs.Confirm(this, $"Delete job '{job.Name}'?\n\nBackups already stored on destinations are not deleted."))
        {
            _services.Jobs.Delete(job.Id);
            RefreshJobs();
        }
    }

    private void ToggleJob()
    {
        if (SelectedJob is { } job)
        {
            job.Enabled = !job.Enabled;
            _services.Jobs.Save(job);
            RefreshJobs();
        }
    }

    private void RunNow()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        _services.Runs.RequestRun(job.Id);
        if (WindowsServiceManager.GetStatus() != ServiceControllerStatus.Running)
        {
            Dialogs.Info(this, "The run was queued, but the Storix service is not running. It will start as soon as the service starts (see the Service tab).");
        }
        else
        {
            Dialogs.Info(this, $"'{job.Name}' was queued and will start within a few seconds. Follow it in the History tab.");
        }
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
        _historyFilter.SelectedIndexChanged += (_, _) => RefreshHistory();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4) };
        top.Controls.Add(new Label { Text = "Job:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        top.Controls.Add(_historyFilter);
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
            var item = new ListViewItem(
            [
                run.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                run.JobName,
                run.Trigger.ToString(),
                run.Status.ToString(),
                run.Duration?.ToString(@"hh\:mm\:ss") ?? "-",
                run.SizeBytes is { } size ? FormatSize(size) : "-",
                run.FileName ?? "-",
                run.Message ?? string.Empty,
            ])
            {
                Tag = run,
                UseItemStyleForSubItems = false,
                Selected = run.Id == selectedId,
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

    // ---------------------------------------------------------------- Settings

    private Control BuildSettingsTab()
    {
        var grid = Ui.Form();
        grid.Row(null, new Label { Text = "Engine", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        grid.Row("Max concurrent jobs", _maxConcurrent);
        grid.Row("Staging folder (empty = default)", _staging);
        grid.Row("Keep history (days, 0 = forever)", _historyDays);
        grid.Row(null, new Label { Text = "SMTP (e-mail notifications)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        grid.Row(null, _smtpEnabled);
        grid.Row("Host", _smtpHost);
        grid.Row("Port", _smtpPort);
        grid.Row(null, _smtpSsl);
        grid.Row("User name", _smtpUser);
        grid.Row("Password", _smtpPassword);
        grid.Row("From address", _smtpFrom);
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
        _smtpEnabled.Checked = settings.Smtp.Enabled;
        _smtpHost.Text = settings.Smtp.Host;
        _smtpPort.Value = Math.Clamp(settings.Smtp.Port, 1, 65_535);
        _smtpSsl.Checked = settings.Smtp.UseSsl;
        _smtpUser.Text = settings.Smtp.UserName;
        _smtpPassword.Text = settings.Smtp.Password;
        _smtpFrom.Text = settings.Smtp.From;
    }

    private void SaveSettings()
    {
        var settings = new AppSettings
        {
            MaxConcurrentJobs = (int)_maxConcurrent.Value,
            StagingDirectory = string.IsNullOrWhiteSpace(_staging.Text) ? null : _staging.Text.Trim(),
            HistoryRetentionDays = (int)_historyDays.Value,
            Smtp = new SmtpSettings
            {
                Enabled = _smtpEnabled.Checked,
                Host = _smtpHost.Text.Trim(),
                Port = (int)_smtpPort.Value,
                UseSsl = _smtpSsl.Checked,
                UserName = string.IsNullOrWhiteSpace(_smtpUser.Text) ? null : _smtpUser.Text.Trim(),
                Password = string.IsNullOrEmpty(_smtpPassword.Text) ? null : _smtpPassword.Text,
                From = _smtpFrom.Text.Trim(),
            },
        };

        _services.Settings.Save(settings);
        Dialogs.Info(this, "Settings saved.");
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
                if (Dialogs.Confirm(this, "Uninstall the Storix service? Scheduled backups will stop."))
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
        grid.Fill();
        return grid;
    }

    private async void ServiceAction(Action action, string verb)
    {
        UseWaitCursor = true;
        try
        {
            await Task.Run(action);
            Dialogs.Info(this, $"The service was {verb}.");
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
            Dialogs.Info(this, $"Exported {_jobList.Count} job(s).");
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
            switch (_tabs.SelectedIndex)
            {
                case 0:
                    // Keep the selection stable while the user is working.
                    if (!silent || !_jobs.Focused)
                    {
                        RefreshJobs();
                    }

                    break;
                case 1:
                    RefreshHistory();
                    break;
            }
        }
        catch (Exception) when (silent)
        {
            // Database busy: try again on the next tick.
        }
    }

    private static Color StatusColor(RunStatus status) => status switch
    {
        RunStatus.Succeeded => Color.ForestGreen,
        RunStatus.PartiallySucceeded => Color.DarkOrange,
        RunStatus.Running => Color.RoyalBlue,
        RunStatus.Cancelled => SystemColors.GrayText,
        _ => Color.Firebrick,
    };

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

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
