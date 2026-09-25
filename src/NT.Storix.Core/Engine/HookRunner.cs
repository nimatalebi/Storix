using System.Diagnostics;
using System.Text;

namespace NT.Storix.Core.Engine;

public sealed record HookResult(int ExitCode, string Output, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>Runs pre/post job commands with a timeout, capturing their output.</summary>
public static class HookRunner
{
    private const int MaxOutput = 8_000;

    public static async Task<HookResult> RunAsync(string command, IReadOnlyDictionary<string, string?> environment, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // cmd.exe does not understand the escaping used by ArgumentList: pass the command line verbatim.
        // With /s, cmd strips the outer quotes and runs the rest exactly as written.
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command}\"")
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.WorkingDirectory = AppContext.BaseDirectory;
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value ?? string.Empty;
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var sync = new Lock();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (sync)
            {
                if (output.Length < MaxOutput)
                {
                    output.AppendLine(line.Length > 1000 ? line[..1000] : line);
                }
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new HookResult(-1, output.ToString().TrimEnd(), TimedOut: true);
        }

        process.WaitForExit(); // Flush redirected output.
        return new HookResult(process.ExitCode, output.ToString().TrimEnd(), TimedOut: false);
    }
}
