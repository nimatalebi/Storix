using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

/// <summary>Run the service as LocalSystem, NetworkService, a dedicated user or a gMSA, with minimal rights.</summary>
internal sealed class ServiceAccountForm : Form
{
    private readonly RadioButton _system = new() { Text = "LocalSystem (default, full access)", AutoSize = true };
    private readonly RadioButton _network = new() { Text = "Network Service (limited local rights)", AutoSize = true };
    private readonly RadioButton _custom = new() { Text = "Dedicated account or gMSA", AutoSize = true };
    private readonly TextBox _account = new() { PlaceholderText = @"DOMAIN\storix-svc or DOMAIN\storix-gmsa$" };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, PlaceholderText = "Leave empty for a gMSA" };
    private readonly CheckBox _backupOperators = new() { Text = "Add to Backup Operators (read all files without file permissions)", AutoSize = true, Checked = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

    public ServiceAccountForm()
    {
        Text = "Service account";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(620, 470);

        var current = ServiceAccount.Current() ?? ServiceAccount.LocalSystem;
        _system.Checked = current.Equals(ServiceAccount.LocalSystem, StringComparison.OrdinalIgnoreCase);
        _network.Checked = current.Contains("NetworkService", StringComparison.OrdinalIgnoreCase);
        _custom.Checked = !_system.Checked && !_network.Checked;
        if (_custom.Checked)
        {
            _account.Text = current;
        }

        var grid = Ui.Form();
        grid.Row(null, new Label
        {
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            Text = $"Current account: {current}\n\nRunning the service under a dedicated low-privilege account (ideally a group Managed Service Account) limits the damage if it is ever compromised. " +
                   "Storix grants the account 'Log on as a service' and Modify rights on its data folder.",
        });
        grid.Row(null, _system);
        grid.Row(null, _network);
        grid.Row(null, _custom);
        grid.Row("Account", _account);
        grid.Row("Password", _password);
        grid.Row(null, _backupOperators);
        grid.Row(null, Ui.Buttons(Ui.Button("Apply", OnApply), Ui.Button("Close", (_, _) => Close(), 90)));
        grid.Row(null, _log, height: 110);
        grid.Fill();
        Controls.Add(grid);
    }

    private void OnApply(object? sender, EventArgs e)
    {
        var account = _system.Checked ? ServiceAccount.LocalSystem : _network.Checked ? ServiceAccount.NetworkService : _account.Text.Trim();
        if (account.Length == 0)
        {
            Dialogs.Error(this, "Enter the account name.");
            return;
        }

        try
        {
            ServiceAccount.Apply(account, _password.Text, _custom.Checked && _backupOperators.Checked, message => _log.AppendText(message + Environment.NewLine));
            Dialogs.Info(this, "Done. Restart the service to use the new account. Jobs that read SQL Server or network shares need permissions for this account.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
    }
}
