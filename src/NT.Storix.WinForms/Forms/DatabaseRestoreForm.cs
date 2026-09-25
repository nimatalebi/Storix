using NT.Storix.Core.Models;
using NT.Storix.Core.Sources;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

/// <summary>Loads restored database files into a server: RESTORE DATABASE for .bak, mongorestore for .archive.</summary>
internal sealed class DatabaseRestoreForm : Form
{
    private readonly ComboBox _file = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _connection = new();
    private readonly TextBox _database = new();
    private readonly TextBox _dataDirectory = new();
    private readonly CheckBox _replace = new() { Text = "Replace the database if it exists", AutoSize = true };
    private readonly TextBox _tool = new();
    private readonly TextBox _fromDatabase = new();
    private readonly Label _mode = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly Button _run;
    private readonly SourceDefinition? _source;

    public DatabaseRestoreForm(string folder, SourceDefinition? source)
    {
        _source = source;
        Text = "Restore database";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 420);
        MinimizeBox = MaximizeBox = false;

        _file.Items.AddRange(Directory.EnumerateFiles(folder, "*.bak", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(folder, "*.archive", SearchOption.AllDirectories)).Cast<object>().ToArray());
        _file.SelectedIndexChanged += (_, _) => UpdateMode();
        _run = Ui.Button("Restore", OnRun);

        var grid = Ui.Form();
        grid.Row("File", _file);
        grid.Row(null, _mode);
        grid.Row("Connection string", _connection);
        grid.Row("Database name", _database);
        grid.Row("Data folder (SQL, optional)", _dataDirectory);
        grid.Row("mongorestore path (Mongo)", _tool);
        grid.Row("Only database (Mongo, optional)", _fromDatabase);
        grid.Row(null, _replace);
        grid.Row(null, new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(600, 0),
            Text = "SQL Server reads the .bak file itself: the SQL Server service account must be able to read this folder. " +
                   "For a remote server, restore the files to a shared folder first.",
        });
        grid.Row(null, Ui.Buttons(_run, Ui.Button("Close", (_, _) => Close(), 90)));
        grid.Fill();
        Controls.Add(grid);

        if (_file.Items.Count > 0)
        {
            _file.SelectedIndex = 0;
        }
    }

    private bool IsSql => (_file.SelectedItem as string)?.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) == true;

    private void UpdateMode()
    {
        if (_file.SelectedItem is not string path)
        {
            return;
        }

        _mode.Text = IsSql ? "SQL Server: RESTORE DATABASE ... WITH MOVE" : "MongoDB: mongorestore";
        _dataDirectory.Enabled = IsSql;
        _tool.Enabled = _fromDatabase.Enabled = !IsSql;
        _replace.Text = IsSql ? "Replace the database if it exists" : "Drop existing collections before restoring (--drop)";

        if (IsSql)
        {
            _connection.Text = _source?.SqlServer.ConnectionString ?? _connection.Text;
            _database.Text = Path.GetFileNameWithoutExtension(path) + "_restored";
        }
        else
        {
            _connection.Text = _source?.MongoDb.ConnectionString ?? _connection.Text;
            var dump = _source?.MongoDb.MongodumpPath;
            _tool.Text = string.IsNullOrWhiteSpace(dump) ? string.Empty : Path.Combine(Path.GetDirectoryName(dump) ?? string.Empty, OperatingSystem.IsWindows() ? "mongorestore.exe" : "mongorestore");
            _fromDatabase.Text = _source?.MongoDb.Database;
            _database.Text = string.IsNullOrWhiteSpace(_source?.MongoDb.Database) ? string.Empty : _source!.MongoDb.Database + "_restored";
        }
    }

    private async void OnRun(object? sender, EventArgs e)
    {
        if (_file.SelectedItem is not string path || string.IsNullOrWhiteSpace(_connection.Text))
        {
            Dialogs.Error(this, "Select a file and enter a connection string.");
            return;
        }

        if (IsSql && string.IsNullOrWhiteSpace(_database.Text))
        {
            Dialogs.Error(this, "Enter the database name.");
            return;
        }

        var (connection, database, dataDir, replace, tool, from) = (_connection.Text, _database.Text.Trim(), _dataDirectory.Text, _replace.Checked, _tool.Text, _fromDatabase.Text);
        _run.Enabled = false;
        UseWaitCursor = true;
        try
        {
            if (IsSql)
            {
                await Task.Run(() => SqlServerRestorer.RestoreAsync(connection, path, database, dataDir, replace, CancellationToken.None));
            }
            else
            {
                await Task.Run(() => MongoDbRestorer.RestoreAsync(tool, connection, path, NullIfEmpty(from), NullIfEmpty(database), replace, CancellationToken.None));
            }

            Dialogs.Info(this, "The database was restored successfully.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
        finally
        {
            UseWaitCursor = false;
            _run.Enabled = true;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
