using System.Text;
using Microsoft.Extensions.Logging;

namespace NT.Storix.Core.Engine;

/// <summary>Collects the log of a single run (stored with the run history) and forwards it to the application logger.</summary>
public sealed class RunLog(ILogger logger, string jobName)
{
    private readonly StringBuilder _buffer = new();
    private readonly Lock _sync = new();

    public void Info(string message) => Write(LogLevel.Information, message);

    public void Warn(string message) => Write(LogLevel.Warning, message);

    public void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, exception is null ? message : $"{message} {exception.GetType().Name}: {exception.Message}", exception);

    public override string ToString()
    {
        lock (_sync)
        {
            return _buffer.ToString();
        }
    }

    private void Write(LogLevel level, string message, Exception? exception = null)
    {
        lock (_sync)
        {
            _buffer.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append(" [").Append(level switch { LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "INF" }).Append("] ")
                .AppendLine(message);
        }

        logger.Log(level, exception, "[{Job}] {Message}", jobName, message);
    }
}
