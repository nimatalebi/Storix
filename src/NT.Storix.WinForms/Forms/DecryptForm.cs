using NT.Storix.Core.Processing;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

/// <summary>Restores an encrypted backup (<c>.zip.aes</c>) back to a regular ZIP file.</summary>
internal sealed class DecryptForm : Form
{
    private readonly TextBox _input = new();
    private readonly TextBox _output = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Height = 16 };
    private readonly Button _decrypt;

    public DecryptForm()
    {
        Text = "Decrypt backup";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(600, 230);

        _decrypt = Ui.Button("Decrypt", OnDecrypt);
        var grid = Ui.Form();
        grid.Row("Encrypted file", PathRow(_input, BrowseInput));
        grid.Row("Output ZIP", PathRow(_output, BrowseOutput));
        grid.Row("Password", _password);
        grid.Row(null, _progress);
        grid.Row(null, Ui.Buttons(_decrypt, Ui.Button("Close", (_, _) => Close())));
        grid.Fill();
        Controls.Add(grid);
    }

    private static Control PathRow(TextBox box, EventHandler browse)
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, Height = 30, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        box.Dock = DockStyle.Fill;
        panel.Controls.Add(box, 0, 0);
        panel.Controls.Add(Ui.Button("...", browse, 36), 1, 0);
        return panel;
    }

    private void BrowseInput(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Filter = "Storix encrypted backup (*.aes)|*.aes|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _input.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(_output.Text))
            {
                _output.Text = dialog.FileName.EndsWith(AesFileEncryptor.FileExtension, StringComparison.OrdinalIgnoreCase)
                    ? dialog.FileName[..^AesFileEncryptor.FileExtension.Length]
                    : dialog.FileName + ".zip";
            }
        }
    }

    private void BrowseOutput(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog { Filter = "ZIP archive (*.zip)|*.zip", FileName = Path.GetFileName(_output.Text) };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _output.Text = dialog.FileName;
        }
    }

    private async void OnDecrypt(object? sender, EventArgs e)
    {
        if (!File.Exists(_input.Text) || string.IsNullOrWhiteSpace(_output.Text) || _password.Text.Length == 0)
        {
            Dialogs.Error(this, "Select the encrypted file, the output file and enter the password.");
            return;
        }

        if (File.Exists(_output.Text) && !Dialogs.Confirm(this, "The output file already exists. Overwrite it?"))
        {
            return;
        }

        _decrypt.Enabled = false;
        _progress.Visible = true;
        try
        {
            var (input, output, password) = (_input.Text, _output.Text, _password.Text);
            await Task.Run(async () =>
            {
                File.Delete(output);
                await AesFileEncryptor.DecryptAsync(input, output, password, CancellationToken.None);
            });
            Dialogs.Info(this, "The backup was decrypted successfully.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
        finally
        {
            _progress.Visible = false;
            _decrypt.Enabled = true;
        }
    }
}
