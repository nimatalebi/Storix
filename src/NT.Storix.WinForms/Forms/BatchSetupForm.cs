using NT.Storix.Core;
using NT.Storix.Core.Configuration;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

/// <summary>
/// "Set up several backups at once": pick websites, databases or folders, one destination and one schedule, and
/// get one job per item with staggered start times (e.g. 3 websites + 3 MongoDB databases to Google Drive).
/// </summary>
internal sealed class BatchSetupForm : Form
{
    private sealed record ItemView(BatchItem Item)
    {
        public override string ToString() => Item.Name == Item.Value ? Item.Name : $"{Item.Name}  —  {Item.Value}";
    }

    private sealed record DestinationView(DestinationDefinition Destination)
    {
        public override string ToString() => $"{Destination.Name} ({Destination.Kind})";
    }

    private readonly IDestinationFactory _factory;
    private readonly RadioButton _websites = new() { Text = "Websites (IIS sites of this server)", AutoSize = true, Checked = true };
    private readonly RadioButton _mongo = new() { Text = "MongoDB databases", AutoSize = true };
    private readonly RadioButton _sql = new() { Text = "SQL Server databases", AutoSize = true };
    private readonly RadioButton _folders = new() { Text = "Folders", AutoSize = true };
    private readonly TextBox _connection = new();
    private readonly Button _load;
    private readonly Button _addFolder;
    private readonly CheckedListBox _items = new() { CheckOnClick = true, IntegralHeight = false };
    private readonly Label _itemsHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(560, 0) };
    private readonly CheckedListBox _destinations = new() { CheckOnClick = true, IntegralHeight = false };
    private readonly ComboBox _schedule = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Anchor = AnchorStyles.Left };
    private readonly ComboBox _weekday = Ui.EnumCombo(DayOfWeek.Friday);
    private readonly DateTimePicker _start = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 90, Anchor = AnchorStyles.Left };
    private readonly NumericUpDown _stagger = Ui.Number(0, 240, 15);
    private readonly ComboBox _retention = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, Anchor = AnchorStyles.Left };
    private readonly CheckBox _encrypt = new() { Text = "Encrypt the backups (recommended for cloud storage)", AutoSize = true, Checked = true };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly TextBox _passwordConfirm = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _stored = new() { Text = "I have stored the password in a safe place (without it the backups cannot be restored)", AutoSize = true };
    private readonly ListView _preview = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(700, 0) };
    private readonly Button _create;

    public BatchSetupForm(IEnumerable<BackupJob> existingJobs, IDestinationFactory factory)
    {
        Localizer.Attach(this);
        _factory = factory;
        Text = "Set up several backups at once";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(980, 760);
        MinimumSize = new Size(820, 600);

        _load = Ui.Button("Load databases", async (_, _) => await LoadDatabasesAsync(), 140);
        _addFolder = Ui.Button("Add folder...", (_, _) => AddFolder(), 120);

        // Destinations already used by other jobs can be reused (same sign-in); new ones are added with the editor.
        foreach (var destination in existingJobs.SelectMany(j => j.Destinations).GroupBy(d => d.Id).Select(g => g.First()))
        {
            _destinations.Items.Add(new DestinationView(destination));
        }

        _schedule.Items.AddRange(["Every day", "Every week"]);
        _schedule.Format += (_, e) => e.Value = Localizer.T((string)e.ListItem!);
        _schedule.SelectedIndex = 0;
        _start.Value = DateTime.Today.AddHours(2);
        _retention.Items.AddRange([RetentionPreset.Standard, RetentionPreset.Short, RetentionPreset.Long]);
        _retention.Format += (_, e) => e.Value = Localizer.T(e.ListItem switch
        {
            RetentionPreset.Short => "Short: the last 7 backups",
            RetentionPreset.Long => "Long: 14 daily, 8 weekly, 12 monthly and 3 yearly backups",
            _ => "Standard: 7 daily, 4 weekly and 6 monthly backups",
        });
        _retention.SelectedIndex = 0;

        var grid = Ui.Form();
        grid.Row(null, Heading("1. What do you want to back up?"));
        var kinds = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        kinds.Controls.AddRange([_websites, _mongo, _sql, _folders]);
        grid.Row(null, kinds);
        grid.Row("Connection string", _connection);
        grid.Row(null, _itemsHint);
        grid.Row(null, _items, height: 150);
        grid.Row(null, Ui.Buttons(
            _load,
            _addFolder,
            Ui.Button("Select all", (_, _) => SetAll(true), 100),
            Ui.Button("Select none", (_, _) => SetAll(false), 100)));

        grid.Row(null, Heading("2. Where to? (tick one or more destinations)"));
        grid.Row(null, _destinations, height: 80);
        grid.Row(null, Ui.Buttons(Ui.Button("New destination...", (_, _) => NewDestination(), 150)));

        grid.Row(null, Heading("3. When?"));
        grid.Row("Schedule", _schedule);
        grid.Row("Day (weekly)", _weekday);
        grid.Row("First job starts at", _start);
        grid.Row("Minutes between jobs", _stagger);
        grid.Row("Keep", _retention);

        grid.Row(null, Heading("4. Protection"));
        grid.Row(null, _encrypt);
        grid.Row("Encryption password", _password);
        grid.Row("Confirm password", _passwordConfirm);
        grid.Row(null, _stored);

        grid.Row(null, Heading("Jobs that will be created"));
        _preview.Columns.Add("Job", 330);
        _preview.Columns.Add("Starts", 110);
        _preview.Columns.Add("Backs up", 380);
        grid.Row(null, _preview, height: 150);
        grid.Row(null, _error);
        grid.Fill();

        _create = new Button { Text = "Create jobs", Width = 150, Height = 30 };
        _create.Click += (_, _) => CreateJobs();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(8) };
        buttons.Controls.AddRange([cancel, _create]);
        Controls.Add(grid);
        Controls.Add(buttons);
        CancelButton = cancel;

        foreach (var radio in new[] { _websites, _mongo, _sql, _folders })
        {
            radio.CheckedChanged += (_, _) =>
            {
                if (radio.Checked)
                {
                    OnKindChanged();
                }
            };
        }

        _items.ItemCheck += (_, _) => BeginInvoke(UpdatePreview);
        _destinations.ItemCheck += (_, _) => BeginInvoke(UpdatePreview);
        _schedule.SelectedIndexChanged += (_, _) => UpdatePreview();
        _start.ValueChanged += (_, _) => UpdatePreview();
        _stagger.ValueChanged += (_, _) => UpdatePreview();
        _encrypt.CheckedChanged += (_, _) => _password.Enabled = _passwordConfirm.Enabled = _stored.Enabled = _encrypt.Checked;
        Load += (_, _) => OnKindChanged();
    }

    /// <summary>The jobs to save (set when the user clicked "Create jobs").</summary>
    public IReadOnlyList<BackupJob> Jobs { get; private set; } = [];

    private BatchKind Kind => _mongo.Checked ? BatchKind.MongoDbDatabases : _sql.Checked ? BatchKind.SqlServerDatabases : _folders.Checked ? BatchKind.Folders : BatchKind.Websites;

    private Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold), Margin = new Padding(3, 12, 3, 3) };

    private void OnKindChanged()
    {
        _items.Items.Clear();
        var database = Kind is BatchKind.MongoDbDatabases or BatchKind.SqlServerDatabases;
        _connection.Enabled = _load.Enabled = database;
        _addFolder.Enabled = !database;
        switch (Kind)
        {
            case BatchKind.Websites:
                var sites = ServerDiscovery.IisSites();
                foreach (var site in sites)
                {
                    _items.Items.Add(new ItemView(new BatchItem(site.Name, site.PhysicalPath)), false);
                }

                _itemsHint.Text = Localizer.T(sites.Count == 0
                    ? "No IIS sites were found on this server. Add the website folders with \"Add folder...\"."
                    : "Tick the websites to back up. Other folders can be added with \"Add folder...\".");
                break;
            case BatchKind.MongoDbDatabases:
                _connection.Text = "mongodb://localhost:27017";
                _itemsHint.Text = Localizer.T("Enter the connection string (with user and password if needed), click \"Load databases\" and tick the ones to back up. mongodump (MongoDB Database Tools) must be installed.");
                break;
            case BatchKind.SqlServerDatabases:
                _connection.Text = "Server=localhost;Integrated Security=true;TrustServerCertificate=true";
                _itemsHint.Text = Localizer.T("Enter the connection string, click \"Load databases\" and tick the ones to back up.");
                break;
            default:
                _itemsHint.Text = Localizer.T("Add the folders to back up; each folder becomes its own job.");
                break;
        }

        UpdatePreview();
    }

    private async Task LoadDatabasesAsync()
    {
        _load.Enabled = false;
        UseWaitCursor = true;
        _error.Text = string.Empty;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var connection = _connection.Text.Trim();
            var names = Kind == BatchKind.MongoDbDatabases
                ? await Task.Run(() => ServerDiscovery.MongoDbDatabasesAsync(connection, timeout.Token))
                : await Task.Run(() => ServerDiscovery.SqlServerDatabasesAsync(connection, timeout.Token));
            _items.Items.Clear();
            foreach (var name in names.Where(n => Kind != BatchKind.SqlServerDatabases || n is not ("master" or "model" or "msdb")))
            {
                _items.Items.Add(new ItemView(new BatchItem(name, name)), false);
            }

            if (_items.Items.Count == 0)
            {
                _error.Text = Localizer.T("The server has no user databases.");
            }
        }
        catch (Exception ex)
        {
            _error.Text = Localizer.T("Could not list the databases:") + " " + ex.Message;
        }
        finally
        {
            UseWaitCursor = false;
            _load.Enabled = true;
            UpdatePreview();
        }
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { ShowNewFolderButton = false };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var name = new DirectoryInfo(dialog.SelectedPath).Name;
            _items.Items.Add(new ItemView(new BatchItem(string.IsNullOrEmpty(name) ? dialog.SelectedPath : name, dialog.SelectedPath)), true);
            UpdatePreview();
        }
    }

    private void SetAll(bool value)
    {
        for (var i = 0; i < _items.Items.Count; i++)
        {
            _items.SetItemChecked(i, value);
        }

        UpdatePreview();
    }

    private void NewDestination()
    {
        using var editor = new DestinationEditorForm(new DestinationDefinition { Name = "Google Drive", Kind = DestinationKind.GoogleDrive }, _factory);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            _destinations.Items.Add(new DestinationView(editor.Destination), true);
            UpdatePreview();
        }
    }

    private BatchOptions Options() => new()
    {
        Kind = Kind,
        ConnectionString = _connection.Enabled ? _connection.Text.Trim() : null,
        Items = _items.CheckedItems.Cast<ItemView>().Select(v => v.Item).ToList(),
        Destinations = _destinations.CheckedItems.Cast<DestinationView>().Select(v => v.Destination).ToList(),
        Schedule = _schedule.SelectedIndex == 1 ? ScheduleKind.Weekly : ScheduleKind.Daily,
        WeeklyDay = (DayOfWeek)_weekday.SelectedItem!,
        FirstStart = _start.Value.TimeOfDay,
        StaggerMinutes = (int)_stagger.Value,
        Retention = (RetentionPreset)_retention.SelectedItem!,
        EncryptionPassword = _encrypt.Checked ? _password.Text : null,
        RecoveryInfoConfirmed = _stored.Checked,
    };

    private void UpdatePreview()
    {
        _weekday.Enabled = _schedule.SelectedIndex == 1;
        _preview.BeginUpdate();
        _preview.Items.Clear();
        var options = Options();
        if (options.Items.Count > 0)
        {
            foreach (var job in BatchJobs.Create(options))
            {
                var what = job.Source.Kind switch
                {
                    SourceKind.MongoDb => $"MongoDB: {job.Source.MongoDb.Database}",
                    SourceKind.SqlServer => $"SQL Server: {string.Join(", ", job.Source.SqlServer.Databases)}",
                    _ => string.Join(", ", job.Source.Files.Paths),
                };
                _preview.Items.Add(new ListViewItem([job.Name, job.Schedule.TimeOfDay.ToString(@"hh\:mm"), what]));
            }
        }

        _preview.EndUpdate();
        _create.Text = options.Items.Count == 0 ? Localizer.T("Create jobs") : Localizer.F("Create {0} job(s)", options.Items.Count);
    }

    private void CreateJobs()
    {
        var options = Options();
        var problem =
            options.Items.Count == 0 ? "Tick at least one website, database or folder."
            : options.Destinations.Count == 0 ? "Tick a destination, or create one with \"New destination...\"."
            : _encrypt.Checked && string.IsNullOrEmpty(_password.Text) ? "Enter an encryption password (or turn encryption off)."
            : _encrypt.Checked && _password.Text != _passwordConfirm.Text ? "The encryption passwords do not match."
            : _encrypt.Checked && !_stored.Checked ? "Confirm that the encryption password is stored in a safe place."
            : null;
        if (problem is not null)
        {
            _error.Text = Localizer.T(problem);
            return;
        }

        var jobs = BatchJobs.Create(options);
        var errors = jobs.SelectMany(j => NT.Storix.Core.Engine.BackupJobRunner.GetValidationErrors(j).Select(e => $"{j.Name}: {e}")).Distinct().ToList();
        if (errors.Count > 0)
        {
            _error.Text = string.Join(Environment.NewLine, errors.Take(5));
            return;
        }

        Jobs = jobs;
        DialogResult = DialogResult.OK;
        Close();
    }
}
