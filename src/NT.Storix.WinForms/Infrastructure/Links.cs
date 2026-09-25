using System.Diagnostics;

namespace NT.Storix.WinForms.Infrastructure;

internal static class Links
{
    public static void Open(IWin32Window? owner, string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Clipboard.SetText(url);
            Dialogs.Error(owner, $"Could not open the link ({ex.Message}).\n\nThe address was copied to the clipboard:\n{url}");
        }
    }
}
