using System.Security.Cryptography.X509Certificates;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <summary>
/// Windows server configuration: IIS configuration, registry keys (reg export), scheduled tasks (XML) and
/// public certificates of LocalMachine stores.
/// </summary>
public sealed class WindowsSystemSource(WindowsSystemSourceOptions options) : IBackupSource
{
    public async Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows system source is only available on Windows.");
        }

        var root = Path.Combine(context.StagingDirectory, "system");
        Directory.CreateDirectory(root);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (options.IisConfiguration)
        {
            var config = Path.Combine(windows, "System32", "inetsrv", "config");
            if (Directory.Exists(config))
            {
                var target = Path.Combine(root, "iis");
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(config, "*.config", SearchOption.AllDirectories)
                             .Concat(Directory.EnumerateFiles(config, "*.xml", SearchOption.AllDirectories)))
                {
                    var relative = Path.GetRelativePath(config, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
                    File.Copy(file, Path.Combine(target, relative), overwrite: true);
                }

                context.Log.Info("Copied the IIS configuration.");
            }
            else
            {
                context.Log.Warn("IIS is not installed; skipped its configuration.");
            }
        }

        var keys = ExternalTool.SplitList(options.RegistryKeys);
        if (keys.Count > 0)
        {
            var target = Path.Combine(root, "registry");
            Directory.CreateDirectory(target);
            foreach (var key in keys)
            {
                var file = Path.Combine(target, Slug.From(key, "key") + ".reg");
                await ExternalTool.RunAndCheckAsync(Path.Combine(windows, "System32", "reg.exe"), ["export", key, file, "/y"], null, "reg export", file, cancellationToken);
                context.Log.Info($"Exported registry key {key}.");
            }
        }

        if (options.ScheduledTasks)
        {
            var target = Path.Combine(root, "tasks");
            Directory.CreateDirectory(target);
            const string script = """
                $ErrorActionPreference = 'Stop'
                Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\Microsoft\*' } | ForEach-Object {
                    $name = ($_.TaskPath + $_.TaskName).Trim('\') -replace '[\\/:*?"<>|]', '_'
                    Export-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath | Set-Content -LiteralPath (Join-Path $env:STORIX_TARGET "$name.xml") -Encoding UTF8
                }
                """;
            var result = await ExternalTool.RunAsync(Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
                new Dictionary<string, string?> { ["STORIX_TARGET"] = target }, "PowerShell", cancellationToken);
            if (result.ExitCode != 0)
            {
                context.Log.Warn($"Exporting scheduled tasks failed: {result.Output}");
            }
            else
            {
                context.Log.Info($"Exported {Directory.GetFiles(target).Length} scheduled task(s).");
            }
        }

        foreach (var storeName in ExternalTool.SplitList(options.CertificateStores))
        {
            var target = Path.Combine(root, "certificates", Slug.From(storeName, "store"));
            Directory.CreateDirectory(target);
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            foreach (var certificate in store.Certificates)
            {
                await File.WriteAllBytesAsync(Path.Combine(target, certificate.Thumbprint + ".cer"), certificate.Export(X509ContentType.Cert), cancellationToken);
            }

            context.Log.Info($"Exported {store.Certificates.Count} public certificate(s) from LocalMachine\\{storeName} (private keys are not exported).");
        }

        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => new ArchiveEntry(f, "system/" + Path.GetRelativePath(root, f).Replace('\\', '/')))
            .ToList();
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("Nothing to back up: select at least one Windows component.");
        }

        return new SourceSnapshot(entries);
    }
}
