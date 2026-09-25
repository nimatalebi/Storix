using System.Diagnostics;

namespace NT.Storix.Core.Sources;

/// <summary>Runs command-line dump tools (pg_dump, mysqldump, redis-cli...) with secrets passed through the environment.</summary>
public static class ExternalTool
{
    public sealed record Result(int ExitCode, string Output);

    public static async Task<Result> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        string displayName,
        CancellationToken cancellationToken,
        string? standardInputFile = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInputFile is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is not null)
            {
                start.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"{displayName} was not found ('{fileName}'). Install it or set its full path in the job.", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (standardInputFile is not null)
        {
            await using (var input = File.OpenRead(standardInputFile))
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            }

            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = (await stdout + await stderr).Trim();
        return new Result(process.ExitCode, output.Length > 4000 ? "..." + output[^4000..] : output);
    }

    /// <summary>Runs the tool and throws when it fails or does not produce <paramref name="expectedFile"/>.</summary>
    public static async Task RunAndCheckAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        string displayName,
        string? expectedFile,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(fileName, arguments, environment, displayName, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{displayName} exited with code {result.ExitCode}: {result.Output}");
        }

        if (expectedFile is not null && (!File.Exists(expectedFile) || new FileInfo(expectedFile).Length == 0))
        {
            throw new InvalidOperationException($"{displayName} did not produce '{Path.GetFileName(expectedFile)}'. {result.Output}");
        }
    }

    public static string Tool(string? configuredPath, string defaultName) =>
        string.IsNullOrWhiteSpace(configuredPath) ? defaultName : configuredPath.Trim();

    public static List<string> SplitList(string? value) =>
        (value ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
