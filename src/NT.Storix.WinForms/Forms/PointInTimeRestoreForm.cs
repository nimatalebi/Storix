using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

/// <summary>Restores a SQL Server database from a full/differential/log chain to the latest state or a point in time.</summary>
internal sealed class PointInTimeRestoreForm : Form
{
    private readonly SqlBackupRepository _sqlBackups;
    private readonly JobRepository _jobs;
    private readonly IDestinationFactory _destinations;

    private readonly ComboBox _database = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _latest = new() { Text = "Latest possible state", AutoSize = true, Checked = true };
    private readonly DateTimePicker _pointInTime = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 180, Anchor = AnchorStyles.Left, Enabled = false };
    private readonly ListView _chain = new() { View = View.Details, FullRowSelect = true };
    private readonly TextBox _connection = new();
    private readonly TextBox _newName = new();
    private readonly TextBox _folder = new();
    private readonly TextBox _dataDirectory = new();
    private readonly CheckBox _replace = new() { Text = "Replace the database if it exists", AutoSize = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _restore;
    private IReadOnlyList<SqlBackupInfo> _plan = [];

    public PointInTimeRestoreForm(SqlBackupRepository sqlBackups, JobRepository jobs, IDestinationFactory destinations)
    {
        Localizer.Attach(this);
        _sqlBackups = sqlBackups;
        _jobs = jobs;
        _destinations = destinations;

        Text = "SQL Server point-in-time restore";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(780, 720);
        MinimizeBox = false;

        foreach (var (server, database) in sqlBackups.ListDatabases())
        {
            _database.Items.Add(new DatabaseItem(server, database));
        }

        _chain.Columns.Add("Type", 100);
        _chain.Columns.Add("Finished", 150);
        _chain.Columns.Add("Job", 150);
        _chain.Columns.Add("Archive", 330);
        _latest.CheckedChanged += (_, _) => _pointInTime.Enabled = !_latest.Checked;
        _database.SelectedIndexChanged += (_, _) => OnDatabaseChanged();
        _restore = Ui.Button("Restore", OnRestore);
        _restore.Enabled = false;

        var grid = Ui.Form();
        grid.Row(null, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(740, 0),
            Text = "Uses full, differential and transaction-log backups made by Storix (full backups must have COPY_ONLY disabled). " +
                   "The needed archives are downloaded from each job's first destination and restored in order.",
        });
        grid.Row("Database", _database);
        grid.Row(null, _latest);
        grid.Row("Restore to (local time)", _pointInTime);
        grid.Row(null, Ui.Buttons(Ui.Button("Plan", (_, _) => OnPlan())));
        grid.Row(null, _chain, height: 140);
        grid.Row("Connection string", _connection);
        grid.Row("New database name", _newName);
        grid.Row("Work folder (readable by SQL Server)", RestoreForm.PathRow(_folder, (_, _) =>
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _folder.Text = dialog.SelectedPath;
            }
        }));
        grid.Row("Data folder (optional)", _dataDirectory);
        grid.Row(null, _replace);
        grid.Row(null, Ui.Buttons(_restore, Ui.Button("Close", (_, _) => Close(), 90)));
        grid.Row(null, _log, height: 120);
        grid.Fill();
        Controls.Add(grid);

        if (_database.Items.Count > 0)
        {
            _database.SelectedIndex = 0;
        }
        else
        {
            Log("No SQL Server backups with chain information have been recorded yet.");
        }
    }

    private sealed record DatabaseItem(string Server, string Database)
    {
        public override string ToString() => $"{Database}  ({Server})";
    }

    private void OnDatabaseChanged()
    {
        if (_database.SelectedItem is not DatabaseItem item)
        {
            return;
        }

        _newName.Text = item.Database + "_restored";
        var job = _sqlBackups.GetBackups(item.Server, item.Database).Select(b => _jobs.Get(b.JobId)).FirstOrDefault(j => j is not null);
        if (job is not null)
        {
            _connection.Text = job.Source.SqlServer.ConnectionString;
            _folder.Text = job.Source.SqlServer.BackupDirectory;
        }

        _chain.Items.Clear();
        _restore.Enabled = false;
    }

    private void OnPlan()
    {
        if (_database.SelectedItem is not DatabaseItem item)
        {
            return;
        }

        try
        {
            DateTimeOffset? stopAt = _latest.Checked ? null : new DateTimeOffset(_pointInTime.Value).ToUniversalTime();
            _plan = SqlRestoreChain.Plan(_sqlBackups.GetBackups(item.Server, item.Database), stopAt);
            _chain.Items.Clear();
            foreach (var backup in _plan)
            {
                _chain.Items.Add(new ListViewItem([backup.Type.ToString(), backup.BackupFinish.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), _jobs.Get(backup.JobId)?.Name ?? "(deleted job)", backup.ArchiveName ?? "-"]));
            }

            _restore.Enabled = true;
            Log($"Plan: {_plan.Count} backup(s).");
        }
        catch (InvalidOperationException ex)
        {
            _restore.Enabled = false;
            Dialogs.Error(this, ex.Message);
        }
    }

    private async void OnRestore(object? sender, EventArgs e)
    {
        if (_plan.Count == 0 || string.IsNullOrWhiteSpace(_folder.Text) || string.IsNullOrWhiteSpace(_newName.Text) || string.IsNullOrWhiteSpace(_connection.Text))
        {
            Dialogs.Error(this, "Plan the restore and fill in the connection string, the new database name and the work folder.");
            return;
        }

        DateTimeOffset? stopAt = _latest.Checked ? null : new DateTimeOffset(_pointInTime.Value).ToUniversalTime();
        var (connection, name, folder, dataDir, replace) = (_connection.Text, _newName.Text.Trim(), _folder.Text.Trim(), _dataDirectory.Text, _replace.Checked);
        var plan = _plan;
        var status = new Progress<string>(Log);
        _restore.Enabled = false;
        UseWaitCursor = true;
        try
        {
            await Task.Run(async () =>
            {
                var restore = new RestoreService(_destinations);
                var files = new List<(string, SqlBackupType)>();
                for (var i = 0; i < plan.Count; i++)
                {
                    var backup = plan[i];
                    var job = _jobs.Get(backup.JobId) ?? throw new InvalidOperationException($"The job that created {backup.ArchiveName} no longer exists.");
                    var destination = job.Destinations.FirstOrDefault(d => d.Enabled) ?? throw new InvalidOperationException($"Job '{job.Name}' has no enabled destination.");
                    var target = Path.Combine(folder, $"storix-pitr-{i:00}-{backup.Type}");
                    var secret = job.Processing.Encrypt ? EncryptionSecret.Resolve(job.Processing) : null;
                    await restore.RestoreFromDestinationAsync(destination, backup.ArchiveName!, new RestoreRequest(target, secret, Overwrite: true), status, CancellationToken.None);
                    files.Add((Path.Combine(target, backup.EntryName.Replace('/', Path.DirectorySeparatorChar)), backup.Type));
                }

                ((IProgress<string>)status).Report($"Restoring {files.Count} backup(s) into [{name}]...");
                await SqlServerRestorer.RestoreChainAsync(connection, files, name, dataDir, replace, stopAt, CancellationToken.None);
            });

            Log("Done.");
            Dialogs.Info(this, $"Database [{name}] was restored{(stopAt is null ? string.Empty : $" to {stopAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}")}. You can delete the storix-pitr-* folders in the work folder.");
        }
        catch (Exception ex)
        {
            Log("Failed: " + ex.Message);
            Dialogs.Error(this, ex.Message);
        }
        finally
        {
            UseWaitCursor = false;
            _restore.Enabled = true;
        }
    }

    private void Log(string message) => _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
}
