using System.Runtime.InteropServices;

namespace NT.Storix.Core.Engine;

/// <summary>Detects metered internet connections (mobile hotspots, capped plans) through the Windows Network List Manager.</summary>
public static class MeteredConnection
{
    // NLM_CONNECTION_COST flags.
    private const uint Fixed = 0x2;
    private const uint Variable = 0x4;
    private const uint OverDataLimit = 0x10000;
    private const uint Roaming = 0x40000;

    /// <summary>True when Windows reports the current connection as metered. False when unknown or not on Windows.</summary>
    public static bool IsMetered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null || Activator.CreateInstance(type) is not INetworkCostManager manager)
            {
                return false;
            }

            try
            {
                manager.GetCost(out var cost, IntPtr.Zero);
                return IsMeteredCost(cost);
            }
            finally
            {
                Marshal.ReleaseComObject(manager);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsMeteredCost(uint cost) => (cost & (Fixed | Variable | OverDataLimit | Roaming)) != 0;

    [ComImport]
    [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, IntPtr destinationAddress);
    }
}
