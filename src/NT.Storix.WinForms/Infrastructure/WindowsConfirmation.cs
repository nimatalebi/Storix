using System.Runtime.InteropServices;
using System.Text;

namespace NT.Storix.WinForms.Infrastructure;

/// <summary>
/// Asks the signed-in user to confirm a sensitive action with their Windows password (Windows security dialog).
/// </summary>
internal static class WindowsConfirmation
{
    private const int CreduiwinGeneric = 0x1;
    private const int CreduiwinEnumerateCurrentUser = 0x200;

    /// <summary>Returns true when the user entered valid credentials of their own account.</summary>
    public static bool Verify(IWin32Window owner, string reason)
    {
        var info = new CredUiInfo
        {
            Size = Marshal.SizeOf<CredUiInfo>(),
            Parent = owner.Handle,
            MessageText = reason,
            CaptionText = "Storix - confirm with your Windows password",
        };

        uint package = 0;
        var result = CredUIPromptForWindowsCredentials(ref info, 0, ref package, IntPtr.Zero, 0, out var buffer, out var size, IntPtr.Zero, CreduiwinGeneric | CreduiwinEnumerateCurrentUser);
        if (result != 0)
        {
            return false; // Cancelled.
        }

        try
        {
            var user = new StringBuilder(256);
            var domain = new StringBuilder(256);
            var password = new StringBuilder(256);
            int userLength = user.Capacity, domainLength = domain.Capacity, passwordLength = password.Capacity;
            if (!CredUnPackAuthenticationBuffer(0, buffer, size, user, ref userLength, domain, ref domainLength, password, ref passwordLength))
            {
                return false;
            }

            var name = user.ToString();
            var domainName = domain.ToString();
            if (name.Contains('\\'))
            {
                domainName = name.Split('\\')[0];
                name = name.Split('\\')[1];
            }

            // Only the current user may confirm.
            if (!string.Equals(name, Environment.UserName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!LogonUser(name, string.IsNullOrEmpty(domainName) ? Environment.UserDomainName : domainName, password.ToString(), 2 /* INTERACTIVE */, 0, out var token))
            {
                return false;
            }

            CloseHandle(token);
            password.Clear();
            return true;
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredUiInfo
    {
        public int Size;
        public IntPtr Parent;
        public string MessageText;
        public string CaptionText;
        public IntPtr Banner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentials(ref CredUiInfo info, int authError, ref uint authPackage, IntPtr inBuffer, uint inBufferSize, out IntPtr outBuffer, out uint outBufferSize, IntPtr save, int flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern bool CredUnPackAuthenticationBuffer(int flags, IntPtr buffer, uint size, StringBuilder userName, ref int userNameLength, StringBuilder domainName, ref int domainNameLength, StringBuilder password, ref int passwordLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string userName, string domain, string password, int logonType, int logonProvider, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
