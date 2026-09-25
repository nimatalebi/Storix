using System.Diagnostics;
using System.ServiceProcess;
using NT.Storix.Core;

namespace NT.Storix.WinForms.Infrastructure;

/// <summary>Installs and controls the Storix Windows service.</summary>
internal static class WindowsServiceManager
{
    public const string ServiceExecutable = "Storix.Service.exe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static ServiceControllerStatus? GetStatus()
    {
        try
        {
            using var controller = new ServiceController(StorixPaths.ServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null; // Not installed.
        }
    }

    public static string DescribeStatus() => GetStatus() switch
    {
        null => "Not installed",
        var status => status.Value.ToString(),
    };

    public static void Start()
    {
        using var controller = new ServiceController(StorixPaths.ServiceName);
        if (controller.Status != ServiceControllerStatus.Running)
        {
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, Timeout);
        }
    }

    public static void Stop()
    {
        using var controller = new ServiceController(StorixPaths.ServiceName);
        if (controller.Status != ServiceControllerStatus.Stopped)
        {
            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
        }
    }

    public static void Install()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, ServiceExecutable);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException($"{ServiceExecutable} was not found next to the manager. Publish both applications into the same folder.", executable);
        }

        Sc($"create {StorixPaths.ServiceName} binPath= \"\\\"{executable}\\\"\" start= delayed-auto DisplayName= \"Storix Backup Agent\"");
        Sc($"description {StorixPaths.ServiceName} \"Storix scheduled backup agent (files, SQL Server, MongoDB).\"");

        // Restart automatically after a crash (crash recovery).
        Sc($"failure {StorixPaths.ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/300000");
    }

    public static void Uninstall()
    {
        if (GetStatus() is { } status && status != ServiceControllerStatus.Stopped)
        {
            Stop();
        }

        Sc($"delete {StorixPaths.ServiceName}");
    }

    private static void Sc(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe {arguments.Split(' ')[0]} failed ({process.ExitCode}): {output.Trim()}");
        }
    }
}
