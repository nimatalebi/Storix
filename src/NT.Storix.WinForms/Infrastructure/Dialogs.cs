namespace NT.Storix.WinForms.Infrastructure;

internal static class Dialogs
{
    public static void Error(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "Storix", MessageBoxButtons.OK, MessageBoxIcon.Error);

    public static void Info(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "Storix", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static bool Confirm(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "Storix", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
}
