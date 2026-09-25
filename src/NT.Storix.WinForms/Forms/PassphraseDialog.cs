using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

/// <summary>Asks for a passphrase, optionally with confirmation.</summary>
internal sealed class PassphraseDialog : Form
{
    private readonly TextBox _passphrase = new() { UseSystemPasswordChar = true };
    private readonly TextBox _confirm = new() { UseSystemPasswordChar = true };
    private readonly bool _requireConfirmation;

    public PassphraseDialog(string title, string message, bool requireConfirmation)
    {
        Localizer.Attach(this);
        _requireConfirmation = requireConfirmation;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(440, requireConfirmation ? 200 : 165);

        var grid = Ui.Form();
        grid.Row(null, new Label { Text = message, AutoSize = true, MaximumSize = new Size(410, 0) });
        grid.Row("Passphrase", _passphrase);
        if (requireConfirmation)
        {
            grid.Row("Confirm", _confirm);
        }

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };
        grid.Row(null, Ui.Buttons(ok, cancel));
        grid.Fill();

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(grid);
    }

    public string Passphrase => _passphrase.Text;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            if (_passphrase.Text.Length == 0)
            {
                Dialogs.Error(this, "Enter a passphrase.");
                e.Cancel = true;
            }
            else if (_requireConfirmation && _passphrase.Text != _confirm.Text)
            {
                Dialogs.Error(this, "The passphrases do not match.");
                e.Cancel = true;
            }
        }

        base.OnFormClosing(e);
    }
}
