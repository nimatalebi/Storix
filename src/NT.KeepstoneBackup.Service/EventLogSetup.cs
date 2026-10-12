using System.Runtime.Versioning;
using NT.KeepstoneBackup.Core;

namespace NT.KeepstoneBackup.Service;

internal static class EventLogSetup
{
    /// <summary>Warnings and errors go to the Windows Application event log with the source "KeepstoneBackup".</summary>
    [SupportedOSPlatform("windows")]
    public static void Add(ILoggingBuilder logging) =>
        logging.AddEventLog(options =>
        {
            options.SourceName = KeepstoneBackupPaths.ServiceName;
            options.Filter = (_, level) => level >= LogLevel.Warning;
        });
}
