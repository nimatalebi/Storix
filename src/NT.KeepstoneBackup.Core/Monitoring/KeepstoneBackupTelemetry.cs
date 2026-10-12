using System.Diagnostics;

namespace NT.KeepstoneBackup.Core.Monitoring;

/// <summary>OpenTelemetry-compatible tracing (System.Diagnostics.Activity) for backup runs.</summary>
public static class KeepstoneBackupTelemetry
{
    public const string SourceName = "NT.KeepstoneBackup";

    public static ActivitySource Source { get; } = new(SourceName, KeepstoneBackupInfo.Version);
}
