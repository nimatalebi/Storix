using System.Management;
using System.Runtime.Versioning;

namespace NT.Storix.Core.Sources;

/// <summary>A point-in-time snapshot of a volume. Disposing it deletes the shadow copy.</summary>
public interface IVolumeSnapshot : IDisposable
{
    /// <summary>Volume root, e.g. <c>C:\</c>.</summary>
    string Volume { get; }

    /// <summary>Translates a path on the live volume to the same path inside the snapshot.</summary>
    string MapPath(string path);
}

/// <summary>
/// Windows Volume Shadow Copy Service (VSS) snapshots, created through WMI (Win32_ShadowCopy). Lets Storix back up
/// files that are open or locked by other programs, in a consistent state. Requires administrator rights
/// (the Storix service runs as LocalSystem).
/// </summary>
public static class VolumeShadowCopy
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    [SupportedOSPlatform("windows")]
    public static IVolumeSnapshot Create(string volumeRoot)
    {
        using var shadowClass = new ManagementClass(@"\\.\root\cimv2", "Win32_ShadowCopy", null);
        using var input = shadowClass.GetMethodParameters("Create");
        input["Volume"] = volumeRoot;
        input["Context"] = "ClientAccessible";

        using var output = shadowClass.InvokeMethod("Create", input, null);
        var code = Convert.ToUInt32(output["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
        if (code != 0)
        {
            throw new InvalidOperationException($"Creating the shadow copy of {volumeRoot} failed: {Describe(code)} (code {code}).");
        }

        var id = (string)output["ShadowID"];
        using var searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", $"SELECT * FROM Win32_ShadowCopy WHERE ID = '{id}'");
        var shadow = searcher.Get().Cast<ManagementObject>().FirstOrDefault()
                     ?? throw new InvalidOperationException("The shadow copy was created but could not be found.");

        return new Snapshot(volumeRoot, (string)shadow["DeviceObject"], shadow);
    }

    private static string Describe(uint code) => code switch
    {
        1 => "access denied (run as administrator / LocalSystem)",
        2 => "invalid argument",
        3 => "the volume was not found",
        4 => "the volume is not supported",
        5 => "unsupported shadow copy context",
        6 => "insufficient storage for the shadow copy",
        7 => "the volume is in use",
        8 => "maximum number of shadow copies reached",
        9 => "another shadow copy operation is in progress",
        10 => "a shadow copy provider had an error",
        11 => "the shadow copy provider is not registered",
        12 => "the shadow copy provider is not in the database",
        _ => "unknown error",
    };

    [SupportedOSPlatform("windows")]
    private sealed class Snapshot(string volume, string device, ManagementObject shadow) : IVolumeSnapshot
    {
        private bool _disposed;

        public string Volume { get; } = volume;

        public string MapPath(string path)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(Volume, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{path}' is not on volume {Volume}.", nameof(path));
            }

            return device.TrimEnd('\\') + "\\" + full[Volume.Length..];
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                shadow.Delete();
            }
            finally
            {
                shadow.Dispose();
            }
        }
    }
}
