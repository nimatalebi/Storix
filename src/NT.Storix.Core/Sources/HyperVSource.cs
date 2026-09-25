using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <summary>Hyper-V virtual machines exported with Export-VM (running VMs use a production checkpoint).</summary>
public sealed class HyperVSource(HyperVSourceOptions options) : IBackupSource
{
    public async Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Hyper-V is only available on Windows.");
        }

        var machines = ExternalTool.SplitList(options.VirtualMachines);
        if (machines.Count == 0)
        {
            throw new InvalidOperationException("No virtual machine selected.");
        }

        var target = Path.Combine(context.StagingDirectory, "hyperv");
        Directory.CreateDirectory(target);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

        foreach (var machine in machines)
        {
            context.Log.Info($"Exporting virtual machine '{machine}'.");
            var result = await ExternalTool.RunAsync(powershell,
                ["-NoProfile", "-NonInteractive", "-Command", "Export-VM -Name $env:STORIX_VM -Path $env:STORIX_TARGET -ErrorAction Stop"],
                new Dictionary<string, string?> { ["STORIX_VM"] = machine, ["STORIX_TARGET"] = target }, "Export-VM", cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Export-VM '{machine}' failed: {result.Output}");
            }
        }

        var entries = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Select(f => new ArchiveEntry(f, "hyperv/" + Path.GetRelativePath(target, f).Replace('\\', '/')))
            .ToList();
        return new SourceSnapshot(entries);
    }
}
