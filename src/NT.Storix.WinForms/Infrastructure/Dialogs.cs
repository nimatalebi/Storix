namespace NT.Storix.WinForms.Infrastructure;

internal static class Dialogs
{
    public static void Error(IWin32Window? owner, string message) =>
        Show(owner, message, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public static void Info(IWin32Window? owner, string message) =>
        Show(owner, message, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static bool Confirm(IWin32Window? owner, string message) =>
        Show(owner, message, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private static DialogResult Show(IWin32Window? owner, string message, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        var options = Localizer.IsPersian ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0;
        return MessageBox.Show(owner, Localizer.T(message), "Storix", buttons, icon, MessageBoxDefaultButton.Button1, options);
    }
}
