using Microsoft.Data.SqlClient;
using NT.Storix.Core;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Scheduling;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

internal sealed class JobEditorForm : Form
{
    private readonly IDestinationFactory _destinationFactory;

    // General
    private readonly TextBox _name = new();
    private readonly TextBox _description = new();
    private readonly CheckBox _enabled = new() { Text = "Enabled (run on schedule)", AutoSize = true };

    // Schedule
    private readonly ComboBox _scheduleKind;
    private readonly DateTimePicker _time = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 100, Anchor = AnchorStyles.Left };
    private readonly CheckBox[] _days = Enum.GetValues<DayOfWeek>().Select(d => new CheckBox { Text = d.ToString()[..3], Tag = d, AutoSize = true }).ToArray();
    private readonly TextBox _cron = new();
    private readonly ComboBox _timeZone = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _nextRuns = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly CheckBox _catchUp = new() { Text = "Run a missed backup when the computer or service starts again", AutoSize = true };

    // Source
    private readonly ComboBox _sourceKind;
    private readonly Panel _sourceHost = new() { Dock = DockStyle.Fill };
    private readonly TextBox _filePaths = Ui.Multiline(120);
    private readonly TextBox _fileExcludes = Ui.Multiline(70);
    private readonly CheckBox _fileRecursive = new() { Text = "Include subfolders", AutoSize = true };
    private readonly CheckBox _fileSkipLocked = new() { Text = "Skip locked files instead of failing", AutoSize = true };
    private readonly TextBox _sqlConnection = new();
    private readonly TextBox _sqlDatabases = Ui.Multiline(90);
    private readonly TextBox _sqlBackupDirectory = new();
    private readonly CheckBox _sqlVerify = new() { Text = "Verify backup (RESTORE VERIFYONLY WITH CHECKSUM)", AutoSize = true };
    private readonly CheckBox _sqlCompression = new() { Text = "Use SQL Server native compression (not on Express)", AutoSize = true };
    private readonly NumericUpDown _sqlTimeout = Ui.Number(0, 86_400);
    private readonly TextBox _mongoTool = new();
    private readonly TextBox _mongoConnection = new();
    private readonly TextBox _mongoDatabase = new();
    private readonly CheckBox _mongoOplog = new() { Text = "Use --oplog (replica set, full dump only)", AutoSize = true };
    private readonly TextBox _mongoExtra = new();
    private readonly Control _filesPanel;
    private readonly Control _sqlPanel;
    private readonly Control _mongoPanel;

    // Processing
    private readonly ComboBox _compression;
    private readonly CheckBox _encrypt = new() { Text = "Encrypt archive with AES-256", AutoSize = true };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly TextBox _passwordConfirm = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _verify = new() { Text = "Verify archive before uploading", AutoSize = true };

    // Destinations
    private readonly ListView _destinations = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };

    // Retention / retry / notifications
    private readonly NumericUpDown _keepLast = Ui.Number(0, 10_000);
    private readonly NumericUpDown _keepDays = Ui.Number(0, 36_500);
    private readonly NumericUpDown _maxAttempts = Ui.Number(1, 20);
    private readonly NumericUpDown _retryDelay = Ui.Number(0, 3_600);
    private readonly NumericUpDown _backoff = Ui.Number(1, 10, decimals: 1);
    private readonly CheckBox _notifySuccess = new() { Text = "On success", AutoSize = true };
    private readonly CheckBox _notifyFailure = new() { Text = "On failure", AutoSize = true };
    private readonly TextBox _notifyEmail = new();
    private readonly NumericUpDown _staleHours = Ui.Number(0, 24 * 90);
    private readonly TextBox _healthUrl = new();

    public JobEditorForm(BackupJob job, IDestinationFactory destinationFactory)
    {
        _destinationFactory = destinationFactory;
        Job = StorixJson.Clone(job);

        Text = $"Backup job - {job.Name}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 620);
        MinimumSize = new Size(640, 520);
        MinimizeBox = false;

        _scheduleKind = Ui.EnumCombo(Job.Schedule.Kind);
        _sourceKind = Ui.EnumCombo(Job.Source.Kind);
        _compression = Ui.EnumCombo(Job.Processing.Compression);

        _filesPanel = BuildFilesPanel();
        _sqlPanel = BuildSqlPanel();
        _mongoPanel = BuildMongoPanel();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("General", BuildGeneralTab()));
        tabs.TabPages.Add(Page("Schedule", BuildScheduleTab()));
        tabs.TabPages.Add(Page("Source", BuildSourceTab()));
        tabs.TabPages.Add(Page("Processing", BuildProcessingTab()));
        tabs.TabPages.Add(Page("Destinations", BuildDestinationsTab()));
        tabs.TabPages.Add(Page("Retention & Retry", BuildPoliciesTab()));
        tabs.TabPages.Add(Page("Notifications", BuildNotificationsTab()));

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42, Padding = new Padding(6) };
        buttons.Controls.AddRange([cancel, ok]);

        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        LoadJob();
    }

    public BackupJob Job { get; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            if (_encrypt.Checked && _password.Text != _passwordConfirm.Text)
            {
                Dialogs.Error(this, "The encryption passwords do not match.");
                e.Cancel = true;
                return;
            }

            SaveJob();
            var errors = BackupJobRunner.GetValidationErrors(Job);
            if (errors.Count > 0)
            {
                Dialogs.Error(this, "Please fix the following:\n\n- " + string.Join("\n- ", errors));
                e.Cancel = true;
                return;
            }
        }

        base.OnFormClosing(e);
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true };
        page.Controls.Add(content);
        return page;
    }

    private Control BuildGeneralTab()
    {
        var grid = Ui.Form();
        grid.Row("Name", _name);
        grid.Row("Description", _description);
        grid.Row(null, _enabled);
        grid.Fill();
        return grid;
    }

    private Control BuildScheduleTab()
    {
        _timeZone.Items.Add("(Local time)");
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            _timeZone.Items.Add(zone.Id);
        }

        var days = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        days.Controls.AddRange(_days);

        var grid = Ui.Form();
        grid.Row("Frequency", _scheduleKind);
        grid.Row("Time", _time);
        grid.Row("Days", days);
        grid.Row("Cron expression", _cron);
        grid.Row(null, new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "Cron format: minute hour day-of-month month day-of-week (e.g. \"0 */6 * * *\" = every 6 hours, \"30 1 * * 1-5\" = 01:30 on weekdays).",
            MaximumSize = new Size(650, 0),
        });
        grid.Row("Time zone", _timeZone);
        grid.Row("Next runs", _nextRuns);
        grid.Row(null, _catchUp);
        grid.Fill();

        _scheduleKind.SelectedIndexChanged += (_, _) => UpdateScheduleUi();
        _time.ValueChanged += (_, _) => UpdateScheduleUi();
        _cron.TextChanged += (_, _) => UpdateScheduleUi();
        _timeZone.SelectedIndexChanged += (_, _) => UpdateScheduleUi();
        foreach (var day in _days)
        {
            day.CheckedChanged += (_, _) => UpdateScheduleUi();
        }

        return grid;
    }

    private Control BuildSourceTab()
    {
        var grid = Ui.Form();
        grid.Row("Source type", _sourceKind);
        grid.Row(null, _sourceHost, height: 440);
        grid.Fill();
        _sourceKind.SelectedIndexChanged += (_, _) => ShowSourcePanel();
        return grid;
    }

    private Control BuildFilesPanel()
    {
        var grid = Ui.Form();
        grid.Padding = Padding.Empty;
        grid.Row("Files and folders\n(one per line)", _filePaths);
        grid.Row(null, Ui.Buttons(
            Ui.Button("Add folder...", (_, _) => AddFolder()),
            Ui.Button("Add files...", (_, _) => AddFiles())));
        grid.Row("Exclude patterns\n(one per line)", _fileExcludes);
        grid.Row(null, new Label { Text = "Wildcards match file or folder names, e.g. *.tmp, *.log, node_modules, bin", AutoSize = true, ForeColor = SystemColors.GrayText });
        grid.Row(null, _fileRecursive);
        grid.Row(null, _fileSkipLocked);
        grid.Fill();
        return grid;
    }

    private Control BuildSqlPanel()
    {
        var grid = Ui.Form();
        grid.Padding = Padding.Empty;
        _sqlConnection.UseSystemPasswordChar = false;
        grid.Row("Connection string", _sqlConnection);
        grid.Row("Databases\n(one per line)", _sqlDatabases);
        grid.Row(null, Ui.Buttons(Ui.Button("Load databases", OnLoadDatabases, 130)));
        grid.Row("Backup directory", _sqlBackupDirectory);
        grid.Row(null, new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(560, 0),
            Text = "Folder where SQL Server writes the .bak file. It must be writable by the SQL Server service and readable by Storix. Leave empty to use the instance default backup folder (local server).",
        });
        grid.Row(null, _sqlVerify);
        grid.Row(null, _sqlCompression);
        grid.Row("Timeout (seconds, 0 = none)", _sqlTimeout);
        grid.Fill();
        return grid;
    }

    private Control BuildMongoPanel()
    {
        var grid = Ui.Form();
        grid.Padding = Padding.Empty;
        grid.Row("mongodump path", _mongoTool);
        grid.Row(null, Ui.Buttons(Ui.Button("Browse...", (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "mongodump|mongodump.exe;mongodump|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _mongoTool.Text = dialog.FileName;
            }
        })));
        grid.Row("Connection string", _mongoConnection);
        grid.Row("Database (empty = all)", _mongoDatabase);
        grid.Row(null, _mongoOplog);
        grid.Row("Extra arguments", _mongoExtra);
        grid.Row(null, new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(560, 0),
            Text = "Requires MongoDB Database Tools. Leave the path empty if mongodump is on PATH.",
        });
        grid.Fill();
        return grid;
    }

    private Control BuildProcessingTab()
    {
        var grid = Ui.Form();
        grid.Row("Compression", _compression);
        grid.Row(null, _encrypt);
        grid.Row("Password", _password);
        grid.Row("Confirm password", _passwordConfirm);
        grid.Row(null, new Label
        {
            AutoSize = true,
            ForeColor = Color.Firebrick,
            MaximumSize = new Size(650, 0),
            Text = "Keep the encryption password safe. Encrypted backups cannot be restored without it.",
        });
        grid.Row(null, _verify);
        grid.Fill();
        _encrypt.CheckedChanged += (_, _) => _password.Enabled = _passwordConfirm.Enabled = _encrypt.Checked;
        return grid;
    }

    private Control BuildDestinationsTab()
    {
        _destinations.Columns.Add("Name", 200);
        _destinations.Columns.Add("Type", 110);
        _destinations.Columns.Add("Target", 300);
        _destinations.Columns.Add("Enabled", 70);
        _destinations.DoubleClick += (_, _) => EditDestination();

        var grid = Ui.Form();
        grid.Row(null, _destinations, height: 380);
        grid.Row(null, Ui.Buttons(
            Ui.Button("Add...", (_, _) => AddDestination()),
            Ui.Button("Edit...", (_, _) => EditDestination()),
            Ui.Button("Remove", (_, _) => RemoveDestination())));
        grid.Fill();
        return grid;
    }

    private Control BuildPoliciesTab()
    {
        var grid = Ui.Form();
        grid.Row(null, new Label { Text = "Retention (applied on every destination after a successful upload)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        grid.Row("Keep last N backups (0 = unlimited)", _keepLast);
        grid.Row("Delete older than N days (0 = never)", _keepDays);
        grid.Row(null, new Label { Text = "The most recent backup is never deleted.", AutoSize = true, ForeColor = SystemColors.GrayText });
        grid.Row(null, new Label { Text = "Retry (database dump and each upload)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        grid.Row("Max attempts", _maxAttempts);
        grid.Row("First retry delay (seconds)", _retryDelay);
        grid.Row("Back-off multiplier", _backoff);
        grid.Fill();
        return grid;
    }

    private Control BuildNotificationsTab()
    {
        var grid = Ui.Form();
        grid.Row(null, new Label { Text = "E-mail notifications (configure SMTP in Settings)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        grid.Row(null, _notifySuccess);
        grid.Row(null, _notifyFailure);
        grid.Row("Recipients (comma separated)", _notifyEmail);
        grid.Row(null, new Label { Text = "Chat and webhook channels are configured in Settings and receive these notifications too.", AutoSize = true, ForeColor = SystemColors.GrayText });
        grid.Row(null, new Label { Text = "Monitoring", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        grid.Row("Alert if no successful backup for (hours, 0 = off)", _staleHours);
        grid.Row("Health-check URL (optional)", _healthUrl);
        grid.Row(null, new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(640, 0),
            Text = "healthchecks.io: paste the ping URL (Storix calls /start, the URL on success and /fail on failure). " +
                   "Uptime Kuma push monitor: use {status} and {message}, e.g. https://kuma/api/push/KEY?status={status}&msg={message}",
        });
        grid.Fill();
        return grid;
    }

    private void LoadJob()
    {
        _name.Text = Job.Name;
        _description.Text = Job.Description;
        _enabled.Checked = Job.Enabled;

        var s = Job.Schedule;
        _time.Value = DateTime.Today.Add(s.TimeOfDay);
        foreach (var day in _days)
        {
            day.Checked = s.DaysOfWeek.Contains((DayOfWeek)day.Tag!);
        }

        _cron.Text = s.CronExpression;
        _catchUp.Checked = s.CatchUpMissedRuns;
        _timeZone.SelectedItem = string.IsNullOrWhiteSpace(s.TimeZoneId) || !_timeZone.Items.Contains(s.TimeZoneId) ? "(Local time)" : s.TimeZoneId;

        var files = Job.Source.Files;
        _filePaths.Lines = files.Paths.ToArray();
        _fileExcludes.Lines = files.ExcludePatterns.ToArray();
        _fileRecursive.Checked = files.IncludeSubdirectories;
        _fileSkipLocked.Checked = files.SkipLockedFiles;

        var sql = Job.Source.SqlServer;
        _sqlConnection.Text = sql.ConnectionString;
        _sqlDatabases.Lines = sql.Databases.ToArray();
        _sqlBackupDirectory.Text = sql.BackupDirectory;
        _sqlVerify.Checked = sql.VerifyBackup;
        _sqlCompression.Checked = sql.NativeCompression;
        _sqlTimeout.Value = Math.Clamp(sql.CommandTimeoutSeconds, 0, 86_400);

        var mongo = Job.Source.MongoDb;
        _mongoTool.Text = mongo.MongodumpPath;
        _mongoConnection.Text = mongo.ConnectionString;
        _mongoDatabase.Text = mongo.Database;
        _mongoOplog.Checked = mongo.UseOplog;
        _mongoExtra.Text = mongo.ExtraArguments;

        var p = Job.Processing;
        _encrypt.Checked = p.Encrypt;
        _password.Text = _passwordConfirm.Text = p.EncryptionPassword;
        _password.Enabled = _passwordConfirm.Enabled = p.Encrypt;
        _verify.Checked = p.VerifyArchive;

        _keepLast.Value = Math.Clamp(Job.Retention.KeepLast, 0, 10_000);
        _keepDays.Value = Math.Clamp(Job.Retention.KeepDays, 0, 36_500);
        _maxAttempts.Value = Math.Clamp(Job.Retry.MaxAttempts, 1, 20);
        _retryDelay.Value = Math.Clamp(Job.Retry.InitialDelaySeconds, 0, 3_600);
        _backoff.Value = (decimal)Math.Clamp(Job.Retry.BackoffMultiplier, 1, 10);

        _notifySuccess.Checked = Job.Notifications.OnSuccess;
        _notifyFailure.Checked = Job.Notifications.OnFailure;
        _notifyEmail.Text = Job.Notifications.EmailTo;
        _staleHours.Value = Math.Clamp(Job.Notifications.AlertIfNoSuccessForHours, 0, 24 * 90);
        _healthUrl.Text = Job.Notifications.HealthCheckUrl;

        RefreshDestinations();
        ShowSourcePanel();
        UpdateScheduleUi();
    }

    private void SaveJob()
    {
        Job.Name = _name.Text.Trim();
        Job.Description = NullIfEmpty(_description.Text);
        Job.Enabled = _enabled.Checked;

        ApplySchedule(Job.Schedule);

        Job.Source.Kind = (SourceKind)_sourceKind.SelectedItem!;
        Job.Source.Files.Paths = _filePaths.Lines();
        Job.Source.Files.ExcludePatterns = _fileExcludes.Lines();
        Job.Source.Files.IncludeSubdirectories = _fileRecursive.Checked;
        Job.Source.Files.SkipLockedFiles = _fileSkipLocked.Checked;

        Job.Source.SqlServer.ConnectionString = NullIfEmpty(_sqlConnection.Text);
        Job.Source.SqlServer.Databases = _sqlDatabases.Lines();
        Job.Source.SqlServer.BackupDirectory = NullIfEmpty(_sqlBackupDirectory.Text);
        Job.Source.SqlServer.VerifyBackup = _sqlVerify.Checked;
        Job.Source.SqlServer.NativeCompression = _sqlCompression.Checked;
        Job.Source.SqlServer.CommandTimeoutSeconds = (int)_sqlTimeout.Value;

        Job.Source.MongoDb.MongodumpPath = NullIfEmpty(_mongoTool.Text);
        Job.Source.MongoDb.ConnectionString = NullIfEmpty(_mongoConnection.Text);
        Job.Source.MongoDb.Database = NullIfEmpty(_mongoDatabase.Text);
        Job.Source.MongoDb.UseOplog = _mongoOplog.Checked;
        Job.Source.MongoDb.ExtraArguments = NullIfEmpty(_mongoExtra.Text);

        Job.Processing.Compression = (ArchiveCompression)_compression.SelectedItem!;
        Job.Processing.Encrypt = _encrypt.Checked;
        Job.Processing.EncryptionPassword = _encrypt.Checked ? _password.Text : null;
        Job.Processing.VerifyArchive = _verify.Checked;

        Job.Retention.KeepLast = (int)_keepLast.Value;
        Job.Retention.KeepDays = (int)_keepDays.Value;
        Job.Retry.MaxAttempts = (int)_maxAttempts.Value;
        Job.Retry.InitialDelaySeconds = (int)_retryDelay.Value;
        Job.Retry.BackoffMultiplier = (double)_backoff.Value;

        Job.Notifications.OnSuccess = _notifySuccess.Checked;
        Job.Notifications.OnFailure = _notifyFailure.Checked;
        Job.Notifications.EmailTo = NullIfEmpty(_notifyEmail.Text);
        Job.Notifications.AlertIfNoSuccessForHours = (int)_staleHours.Value;
        Job.Notifications.HealthCheckUrl = NullIfEmpty(_healthUrl.Text);
    }

    private void ApplySchedule(ScheduleDefinition schedule)
    {
        schedule.Kind = (ScheduleKind)_scheduleKind.SelectedItem!;
        schedule.TimeOfDay = new TimeSpan(_time.Value.Hour, _time.Value.Minute, 0);
        schedule.DaysOfWeek = _days.Where(d => d.Checked).Select(d => (DayOfWeek)d.Tag!).ToList();
        schedule.CronExpression = NullIfEmpty(_cron.Text);
        schedule.TimeZoneId = _timeZone.SelectedIndex <= 0 ? null : (string)_timeZone.SelectedItem!;
        schedule.CatchUpMissedRuns = _catchUp.Checked;
    }

    private void UpdateScheduleUi()
    {
        var kind = (ScheduleKind)_scheduleKind.SelectedItem!;
        _time.Enabled = kind is ScheduleKind.Daily or ScheduleKind.Weekly;
        foreach (var day in _days)
        {
            day.Enabled = kind == ScheduleKind.Weekly;
        }

        _cron.Enabled = kind == ScheduleKind.Cron;

        var preview = new ScheduleDefinition();
        ApplySchedule(preview);
        if (ScheduleCalculator.Validate(preview) is { } error)
        {
            _nextRuns.Text = error;
            return;
        }

        var occurrences = new List<string>();
        var from = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3 && ScheduleCalculator.GetNextOccurrence(preview, from) is { } next; i++)
        {
            occurrences.Add(next.ToLocalTime().ToString("ddd yyyy-MM-dd HH:mm"));
            from = next;
        }

        _nextRuns.Text = occurrences.Count == 0 ? "Manual only" : string.Join(Environment.NewLine, occurrences);
    }

    private void ShowSourcePanel()
    {
        _sourceHost.Controls.Clear();
        var panel = (SourceKind)_sourceKind.SelectedItem! switch
        {
            SourceKind.SqlServer => _sqlPanel,
            SourceKind.MongoDb => _mongoPanel,
            _ => _filesPanel,
        };
        panel.Dock = DockStyle.Fill;
        _sourceHost.Controls.Add(panel);
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select a folder to back up", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _filePaths.Lines = [.. _filePaths.Lines(), dialog.SelectedPath];
        }
    }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "Select files to back up" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _filePaths.Lines = [.. _filePaths.Lines(), .. dialog.FileNames];
        }
    }

    private async void OnLoadDatabases(object? sender, EventArgs e)
    {
        var connectionString = _sqlConnection.Text;
        UseWaitCursor = true;
        try
        {
            var databases = await Task.Run(async () =>
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM sys.databases WHERE name <> 'tempdb' AND state_desc = 'ONLINE' ORDER BY name";
                await using var reader = await command.ExecuteReaderAsync();
                var names = new List<string>();
                while (await reader.ReadAsync())
                {
                    names.Add(reader.GetString(0));
                }

                return names;
            });

            using var picker = new DatabasePickerForm(databases, _sqlDatabases.Lines());
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                _sqlDatabases.Lines = picker.Selected.ToArray();
            }
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, $"Could not connect to SQL Server:\n\n{ex.Message}");
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void RefreshDestinations()
    {
        _destinations.BeginUpdate();
        _destinations.Items.Clear();
        foreach (var destination in Job.Destinations)
        {
            var item = new ListViewItem([destination.Name, destination.Kind.ToString(), DescribeTarget(destination), destination.Enabled ? "Yes" : "No"])
            {
                Tag = destination,
            };
            _destinations.Items.Add(item);
        }

        _destinations.EndUpdate();
    }

    private static string DescribeTarget(DestinationDefinition d) => d.Kind switch
    {
        DestinationKind.LocalFolder => d.LocalFolder.Path,
        DestinationKind.Ftp => $"ftp://{d.Ftp.Host}:{d.Ftp.Port}{d.Ftp.RemotePath}",
        DestinationKind.Sftp => $"sftp://{d.Sftp.Host}:{d.Sftp.Port}/{d.Sftp.RemotePath.TrimStart('/')}",
        DestinationKind.GoogleDrive => $"Drive folder {d.GoogleDrive.FolderId}",
        DestinationKind.S3 => $"s3://{d.S3.BucketName}/{d.S3.Prefix?.Trim('/')}",
        _ => string.Empty,
    };

    private void AddDestination()
    {
        using var editor = new DestinationEditorForm(new DestinationDefinition { Name = $"Destination {Job.Destinations.Count + 1}" }, _destinationFactory);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            Job.Destinations.Add(editor.Destination);
            RefreshDestinations();
        }
    }

    private void EditDestination()
    {
        if (_destinations.SelectedItems.Count == 0)
        {
            return;
        }

        var current = (DestinationDefinition)_destinations.SelectedItems[0].Tag!;
        using var editor = new DestinationEditorForm(current, _destinationFactory);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            Job.Destinations[Job.Destinations.IndexOf(current)] = editor.Destination;
            RefreshDestinations();
        }
    }

    private void RemoveDestination()
    {
        if (_destinations.SelectedItems.Count == 0)
        {
            return;
        }

        var current = (DestinationDefinition)_destinations.SelectedItems[0].Tag!;
        if (Dialogs.Confirm(this, $"Remove destination '{current.Name}'? Existing backups on it are not deleted."))
        {
            Job.Destinations.Remove(current);
            RefreshDestinations();
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class DatabasePickerForm : Form
{
    private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true };

    public DatabasePickerForm(IEnumerable<string> databases, IReadOnlyCollection<string> selected)
    {
        Text = "Select databases";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 420);
        MinimizeBox = MaximizeBox = false;

        foreach (var name in databases)
        {
            _list.Items.Add(name, selected.Contains(name, StringComparer.OrdinalIgnoreCase));
        }

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42, Padding = new Padding(6) };
        buttons.Controls.AddRange([cancel, ok]);

        Controls.Add(_list);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public IEnumerable<string> Selected => _list.CheckedItems.Cast<string>();
}
