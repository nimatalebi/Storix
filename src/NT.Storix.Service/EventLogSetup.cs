using System.Runtime.Versioning;
using NT.Storix.Core;

namespace NT.Storix.Service;

internal static class EventLogSetup
{
    /// <summary>Warnings and errors go to the Windows Application event log with the source "Storix".</summary>
    [SupportedOSPlatform("windows")]
    public static void Add(ILoggingBuilder logging) =>
        logging.AddEventLog(options =>
        {
            options.SourceName = StorixPaths.ServiceName;
            options.Filter = (_, level) => level >= LogLevel.Warning;
        });
}
