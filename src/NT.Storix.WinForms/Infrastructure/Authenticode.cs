using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace NT.Storix.WinForms.Infrastructure;

/// <summary>Authenticode check of downloaded installers (WinVerifyTrust: valid signature, trusted chain, not revoked).</summary>
internal static class Authenticode
{
    public sealed record Result(bool Signed, bool Trusted, string? Subject);

    public static Result Verify(string path)
    {
        string? subject = null;
        try
        {
#pragma warning disable SYSLIB0057 // Only reads the signer certificate of a signed file.
            subject = X509Certificate.CreateFromSignedFile(path).Subject;
#pragma warning restore SYSLIB0057
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return new Result(false, false, null);
        }

        return new Result(true, WinVerifyTrust(path) == 0, subject);
    }

    /// <summary>Subject of the certificate that signed this program, or null when it is not signed.</summary>
    public static string? CurrentSigner()
    {
        var self = Environment.ProcessPath;
        return self is null ? null : Verify(self) is { Trusted: true } result ? result.Subject : null;
    }

    private static uint WinVerifyTrust(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,               // WTD_UI_NONE
                fdwRevocationChecks = 1,      // WTD_REVOKE_WHOLECHAIN
                dwUnionChoice = 1,            // WTD_CHOICE_FILE
                pFile = fileInfoPtr,
                dwStateAction = 0,
                dwProvFlags = 0x80,           // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrustNative(IntPtr.Zero, ref action, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrustNative(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
