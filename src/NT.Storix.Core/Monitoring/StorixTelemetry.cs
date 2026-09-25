using System.Diagnostics;

namespace NT.Storix.Core.Monitoring;

/// <summary>OpenTelemetry-compatible tracing (System.Diagnostics.Activity) for backup runs.</summary>
public static class StorixTelemetry
{
    public const string SourceName = "NT.Storix";

    public static ActivitySource Source { get; } = new(SourceName, StorixInfo.Version);
}
