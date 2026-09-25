using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using NT.Storix.Core;

namespace NT.Storix.WinForms.Infrastructure;

/// <summary>Configures the account the Storix service runs as, with only the rights it needs.</summary>
internal static class ServiceAccount
{
    public const string LocalSystem = "LocalSystem";
    public const string NetworkService = @"NT AUTHORITY\NetworkService";

    /// <summary>Current log-on account of the Storix service (null when not installed).</summary>
    public static string? Current()
    {
        using var searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", $"SELECT StartName FROM Win32_Service WHERE Name = '{StorixPaths.ServiceName}'");
        return searcher.Get().Cast<ManagementObject>().FirstOrDefault()?["StartName"] as string;
    }

    /// <param name="account">LocalSystem, NT AUTHORITY\NetworkService, DOMAIN\user or DOMAIN\gmsa$.</param>
    /// <param name="password">Password for a user account; empty for built-in accounts and gMSAs.</param>
    /// <param name="backupOperators">Add the account to Backup Operators so it can read every file.</param>
    public static void Apply(string account, string? password, bool backupOperators, Action<string> log)
    {
        var builtIn = account.Equals(LocalSystem, StringComparison.OrdinalIgnoreCase);
        if (!builtIn)
        {
            var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));

            GrantLogonAsService(sid);
            log($"Granted 'Log on as a service' to {account}.");

            var data = new DirectoryInfo(StorixPaths.DataDirectory);
            data.Create();
            var acl = data.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            data.SetAccessControl(acl);
            log($"Granted Modify on {data.FullName}.");

            if (backupOperators)
            {
                // S-1-5-32-551 = Backup Operators (the group name is localized).
                var group = new SecurityIdentifier("S-1-5-32-551").Translate(typeof(NTAccount)).Value.Split('\\')[^1];
                Run("net.exe", $"localgroup \"{group}\" \"{account}\" /add", ignoreExitCodes: [2]);
                log($"Added {account} to {group}.");
            }
        }

        var obj = builtIn ? LocalSystem : account;
        var passwordArgument = builtIn || account.EndsWith('$') || account.Equals(NetworkService, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" password= \"{password}\"";
        Run("sc.exe", $"config {StorixPaths.ServiceName} obj= \"{obj}\"{passwordArgument}");
        log($"The service now runs as {obj}. Restart it to apply.");
    }

    private static void Run(string file, string arguments, int[]? ignoreExitCodes = null)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !(ignoreExitCodes ?? []).Contains(process.ExitCode) && !output.Contains("1378", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{Path.GetFileName(file)} failed ({process.ExitCode}): {output.Trim()}");
        }
    }

    private static void GrantLogonAsService(SecurityIdentifier sid)
    {
        var attributes = new LsaObjectAttributes { Length = Marshal.SizeOf<LsaObjectAttributes>() };
        var system = default(LsaUnicodeString);
        var status = LsaOpenPolicy(ref system, ref attributes, 0x00000800 | 0x00000010 /* POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES */, out var policy);
        ThrowIfFailed(status);
        try
        {
            var sidBytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(sidBytes, 0);
            var right = new LsaUnicodeString("SeServiceLogonRight");
            try
            {
                ThrowIfFailed(LsaAddAccountRights(policy, sidBytes, [right], 1));
            }
            finally
            {
                right.Free();
            }
        }
        finally
        {
            LsaClose(policy);
        }
    }

    private static void ThrowIfFailed(uint status)
    {
        if (status != 0)
        {
            throw new System.ComponentModel.Win32Exception((int)LsaNtStatusToWinError(status));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;

        public LsaUnicodeString(string value)
        {
            Buffer = Marshal.StringToHGlobalUni(value);
            Length = (ushort)(value.Length * 2);
            MaximumLength = (ushort)(Length + 2);
        }

        public void Free() => Marshal.FreeHGlobal(Buffer);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaOpenPolicy(ref LsaUnicodeString systemName, ref LsaObjectAttributes objectAttributes, uint desiredAccess, out IntPtr policyHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaAddAccountRights(IntPtr policyHandle, byte[] accountSid, LsaUnicodeString[] userRights, uint countOfRights);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policyHandle);

    [DllImport("advapi32.dll")]
    private static extern uint LsaNtStatusToWinError(uint status);
}
