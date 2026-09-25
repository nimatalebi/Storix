using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Security;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

/// <summary>Restore wizard: pick a backup (from a destination or a file), verify, decrypt and extract it.</summary>
internal sealed class RestoreForm : Form
{
    private readonly RestoreService _restore;
    private readonly IReadOnlyList<BackupJob> _jobs;

    private readonly RadioButton _fromDestination = new() { Text = "From a job's destination", AutoSize = true, Checked = true };
    private readonly RadioButton _fromFile = new() { Text = "From a backup file on this computer or network", AutoSize = true };
    private readonly ComboBox _job = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _destination = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListView _backups = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    private readonly TextBox _file = new();
    private readonly TextBox _target = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly TextBox _keyFile = new();
    private readonly CheckBox _overwrite = new() { Text = "Overwrite existing files", AutoSize = true };
    private readonly CheckBox _verify = new() { Text = "Verify SHA-256 checksum", AutoSize = true, Checked = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _run;
    private readonly Button _database;
    private readonly Label _selection = new() { AutoSize = true, Text = "Restore: all files", ForeColor = SystemColors.GrayText };
    private IReadOnlyList<string>? _include;
    private CancellationTokenSource? _cts;
    private string? _lastRestoreFolder;

    public RestoreForm(IReadOnlyList<BackupJob> jobs, IDestinationFactory destinations, BackupJob? selected = null)
    {
        _jobs = jobs;
        _restore = new RestoreService(destinations);

        Text = "Restore backup";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 700);
        MinimumSize = new Size(640, 600);
        MinimizeBox = false;

        _backups.Columns.Add("Backup", 360);
        _backups.Columns.Add("Created", 160);
        _job.Items.AddRange(jobs.Cast<object>().ToArray());
        _job.Format += (_, e) => e.Value = ((BackupJob)e.ListItem!).Name;
        _job.SelectedIndexChanged += (_, _) => LoadDestinations();
        _destination.SelectedIndexChanged += (_, _) => _backups.Items.Clear();
        _backups.SelectedIndexChanged += (_, _) => SetInclude(null);
        _fromDestination.CheckedChanged += (_, _) => UpdateMode();

        _run = Ui.Button("Restore", OnRestore);
        _database = Ui.Button("Restore database...", OnDatabase, 150);
        _database.Enabled = false;

        var grid = Ui.Form();
        grid.Row(null, _fromDestination);
        grid.Row("Job", _job);
        grid.Row("Destination", _destination);
        grid.Row(null, Ui.Buttons(Ui.Button("Load backups", OnLoadBackups, 130)));
        grid.Row(null, _backups, height: 150);
        grid.Row(null, Ui.Buttons(Ui.Button("Browse files...", OnBrowse, 130), Ui.Button("All files", (_, _) => SetInclude(null), 90), _selection));
        grid.Row(null, _fromFile);
        grid.Row("Backup file", PathRow(_file, BrowseFile));
        grid.Row("Restore to folder", PathRow(_target, BrowseTarget));
        grid.Row("Encryption password", _password);
        grid.Row("Key file (if used)", PathRow(_keyFile, (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Key files (*.key)|*.key|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _keyFile.Text = dialog.FileName;
            }
        }));
        grid.Row(null, _overwrite);
        grid.Row(null, _verify);
        grid.Row(null, Ui.Buttons(_run, _database, Ui.Button("Close", (_, _) => Close(), 90)));
        grid.Row(null, _log, height: 130);
        grid.Fill();
        Controls.Add(grid);

        if (selected is not null && jobs.FirstOrDefault(j => j.Id == selected.Id) is { } job)
        {
            _job.SelectedItem = job;
        }
        else if (jobs.Count > 0)
        {
            _job.SelectedIndex = 0;
        }

        UpdateMode();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosing(e);
    }

    private BackupJob? SelectedJob => _job.SelectedItem as BackupJob;

    private void UpdateMode()
    {
        var fromDestination = _fromDestination.Checked;
        _job.Enabled = _destination.Enabled = _backups.Enabled = fromDestination;
        _file.Enabled = !fromDestination;
        _fromFile.Checked = !fromDestination;
    }

    private void LoadDestinations()
    {
        _destination.Items.Clear();
        _backups.Items.Clear();
        if (SelectedJob is { } job)
        {
            _destination.Items.AddRange(job.Destinations.Cast<object>().ToArray());
            if (_destination.Items.Count > 0)
            {
                _destination.SelectedIndex = 0;
            }

            if (job.Processing.Encrypt && string.IsNullOrEmpty(_password.Text))
            {
                _password.Text = job.Processing.EncryptionPassword;
                _keyFile.Text = job.Processing.EncryptionKeyFile;
            }
        }
    }

    private async void OnLoadBackups(object? sender, EventArgs e)
    {
        if (SelectedJob is not { } job || _destination.SelectedItem is not DestinationDefinition destination)
        {
            return;
        }

        UseWaitCursor = true;
        try
        {
            var backups = await Task.Run(() => _restore.ListBackupsAsync(job, destination, CancellationToken.None));
            _backups.Items.Clear();
            foreach (var backup in backups)
            {
                _backups.Items.Add(new ListViewItem([backup.Name, backup.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")]) { Tag = backup });
            }

            if (_backups.Items.Count > 0)
            {
                _backups.Items[0].Selected = true;
            }
            else
            {
                Log("No backups of this job were found on the destination.");
            }
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

    private async void OnRestore(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_target.Text))
        {
            Dialogs.Error(this, "Choose the folder to restore to.");
            return;
        }

        string? secret;
        try
        {
            secret = EncryptionSecret.Combine(_password.Text, _keyFile.Text);
        }
        catch (FileNotFoundException ex)
        {
            Dialogs.Error(this, ex.Message);
            return;
        }

        var request = new RestoreRequest(_target.Text.Trim(), secret, _overwrite.Checked, _verify.Checked)
        {
            Include = _fromDestination.Checked ? _include : null,
        };
        var status = new Progress<string>(Log);
        _cts = new CancellationTokenSource();
        _run.Enabled = false;
        _database.Enabled = false;

        try
        {
            RestoreResult result;
            if (_fromDestination.Checked)
            {
                if (_destination.SelectedItem is not DestinationDefinition destination || _backups.SelectedItems.Count == 0)
                {
                    Dialogs.Error(this, "Load the backups and select one.");
                    return;
                }

                var backup = (BackupFileInfo)_backups.SelectedItems[0].Tag!;
                result = await Task.Run(() => _restore.RestoreFromDestinationAsync(destination, backup.Name, request, status, _cts.Token));
            }
            else
            {
                if (!File.Exists(_file.Text))
                {
                    Dialogs.Error(this, "Select an existing backup file.");
                    return;
                }

                var file = _file.Text;
                result = await Task.Run(() => RestoreService.RestoreFromFileAsync(file, request, status, _cts.Token));
            }

            _lastRestoreFolder = request.TargetDirectory;
            _database.Enabled = HasDatabaseFiles(_lastRestoreFolder);
            Log($"Done: {result.Files.Count} file(s), {result.TotalBytes:N0} bytes{(result.ChecksumVerified ? ", checksum verified" : string.Empty)}.");
            Dialogs.Info(this, $"Restore completed: {result.Files.Count} file(s) restored to\n{request.TargetDirectory}" +
                               (_database.Enabled ? "\n\nThe backup contains database files. Use 'Restore database...' to load them into a server." : string.Empty));
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled.");
        }
        catch (Exception ex)
        {
            Log("Failed: " + ex.Message);
            Dialogs.Error(this, ex.Message);
        }
        finally
        {
            _run.Enabled = true;
        }
    }

    private void SetInclude(IReadOnlyList<string>? include)
    {
        _include = include;
        _selection.Text = include is null ? "Restore: all files" : $"Restore: {include.Count} selected item(s)";
    }

    private async void OnBrowse(object? sender, EventArgs e)
    {
        if (_destination.SelectedItem is not DestinationDefinition destination || _backups.SelectedItems.Count == 0)
        {
            Dialogs.Error(this, "Load the backups and select one first.");
            return;
        }

        string? secret;
        try
        {
            secret = EncryptionSecret.Combine(_password.Text, _keyFile.Text);
        }
        catch (FileNotFoundException ex)
        {
            Dialogs.Error(this, ex.Message);
            return;
        }

        var backup = (BackupFileInfo)_backups.SelectedItems[0].Tag!;
        UseWaitCursor = true;
        try
        {
            Log($"Reading the file list of {backup.Name}...");
            var index = await Task.Run(() => _restore.GetIndexAsync(destination, backup.Name, secret, CancellationToken.None));
            using var browser = new BackupBrowserForm(index);
            if (browser.ShowDialog(this) == DialogResult.OK)
            {
                SetInclude(browser.Selected);
            }
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

    private void OnDatabase(object? sender, EventArgs e)
    {
        if (_lastRestoreFolder is null)
        {
            return;
        }

        using var form = new DatabaseRestoreForm(_lastRestoreFolder, SelectedJob?.Source);
        form.ShowDialog(this);
    }

    private static bool HasDatabaseFiles(string folder) =>
        Directory.Exists(folder)
        && (Directory.EnumerateFiles(folder, "*.bak", SearchOption.AllDirectories).Any()
            || Directory.EnumerateFiles(folder, "*.archive", SearchOption.AllDirectories).Any());

    private void Log(string message) =>
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");

    private void BrowseFile(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Filter = "Storix backups (*.zip;*.aes;*.manifest.json)|*.zip;*.aes;*.manifest.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _file.Text = dialog.FileName;
            _fromFile.Checked = true;
        }
    }

    private void BrowseTarget(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Restore to", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _target.Text = dialog.SelectedPath;
        }
    }

    internal static Control PathRow(TextBox box, EventHandler browse)
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, Height = 30, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        box.Dock = DockStyle.Fill;
        panel.Controls.Add(box, 0, 0);
        panel.Controls.Add(Ui.Button("...", browse, 36), 1, 0);
        return panel;
    }
}
