using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NT.Storix.Core.Destinations;

/// <summary>Connects to an SMB share (\\server\share) with explicit credentials on Windows.</summary>
public sealed partial class NetworkShare : IDisposable
{
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorAlreadyAssigned = 85;
    private readonly string? _share;

    private NetworkShare(string? share) => _share = share;

    /// <summary>
    /// Connects when <paramref name="path"/> is a UNC path and a user name is given; otherwise does nothing.
    /// </summary>
    public static NetworkShare Connect(string path, string? userName, string? password)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(userName) || !path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new NetworkShare(null);
        }

        var share = ShareRoot(path);
        var resource = new NetResource { Type = 1 /* RESOURCETYPE_DISK */, RemoteName = share };
        var result = WNetAddConnection2(ref resource, password, userName, 0);
        if (result is ErrorSessionCredentialConflict or ErrorAlreadyAssigned)
        {
            // Already connected (possibly with the same credentials): use the existing session.
            return new NetworkShare(null);
        }

        if (result != 0)
        {
            throw new IOException($"Could not connect to {share} as {userName}: {new Win32Exception(result).Message}");
        }

        return new NetworkShare(share);
    }

    /// <summary>\\server\share part of a UNC path.</summary>
    public static string ShareRoot(string path)
    {
        var parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new ArgumentException($"'{path}' is not a UNC path (\\\\server\\share).", nameof(path));
        }

        return $@"\\{parts[0]}\{parts[1]}";
    }

    public void Dispose()
    {
        if (_share is not null && OperatingSystem.IsWindows())
        {
            _ = WNetCancelConnection2(_share, 0, true);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? userName, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
