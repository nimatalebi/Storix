using NT.Storix.Core;
using NT.Storix.Core.Configuration;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Scheduling;
using NT.Storix.Core.Security;

namespace NT.Storix.Cli;

internal static class Commands
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "overwrite", "wait", "dry-run", "no-verify", "json", "settings" };

    public const string Usage = """
        Storix command-line tool

        Backups on disk (no installation or database needed):
          storix verify  <backup> [--password P] [--key-file F]      Check checksum, volumes and decryption
          storix list-files <backup> [--password P] [--key-file F]   List the files inside a backup
          storix restore <backup> --to DIR [--password P] [--key-file F] [--include PATH]... [--overwrite]
          storix decrypt <file.aes> <output> [--password P] [--key-file F]
          storix keygen --out private.pem [--passphrase P]           Create an RSA key pair for public-key encryption
            <backup> is a .zip / .zip.aes file, a .manifest.json or any .partNNNN volume.
            Public-key backups: --key-file private.pem and --password <passphrase of the key>.
            Use --password-env NAME to read the password from an environment variable.

        Jobs (uses the Storix database of this machine):
          storix jobs                              List jobs with schedule and last result
          storix history [job] [--limit N]         Recent runs
          storix run <job> [--wait] [--dry-run]    Queue a backup (processed by the service), or show a dry run
          storix cancel <job>                      Cancel a running backup
          storix drill <job>                       Queue a restore drill
          storix export <file> [--passphrase P] [--settings]
          storix import <file> [--passphrase P] [--settings]
          storix validate <file>                   Check a job file (config as code), ${env:NAME} placeholders allowed
          storix apply <file>                      Import a job file, resolving ${env:NAME} placeholders
          storix templates                         Show job templates

          storix version | help
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            output.WriteLine(Usage);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var a = new Arguments(args.Skip(1), Flags);
        switch (command)
        {
            case "version" or "--version":
                output.WriteLine($"storix {StorixInfo.Version}");
                return 0;
            case "verify":
                return await VerifyAsync(a, output);
            case "list-files":
                return await ListFilesAsync(a, output);
            case "restore":
                return await RestoreAsync(a, output);
            case "decrypt":
                await AesFileEncryptor.DecryptAsync(a.Required(0, "input file"), a.Required(1, "output file"), RequireSecret(a), CancellationToken.None);
                output.WriteLine("Decrypted.");
                return 0;
            case "keygen":
            {
                var file = a.Option("out") ?? throw new CliException("Missing --out <private.pem>.");
                var (publicPem, privatePem) = PrivateKeySecret.GenerateKeyPair(a.Option("passphrase"));
                File.WriteAllText(file, privatePem);
                File.WriteAllText(Path.ChangeExtension(file, null) + ".pub.pem", publicPem);
                output.WriteLine($"Private key: {file}  (keep it offline)");
                output.WriteLine($"Public key:  {Path.ChangeExtension(file, null)}.pub.pem  (import it into the job)");
                output.WriteLine($"Fingerprint: {PrivateKeySecret.Fingerprint(publicPem)}");
                return 0;
            }

            case "jobs":
                return Jobs(output);
            case "history":
                return History(a, output);
            case "run":
                return await RunJobAsync(a, output);
            case "cancel":
                var toCancel = FindJob(a.Required(0, "job name"));
                Services.Runs.RequestCancel(toCancel.Id);
                Services.Audit.Add("job.cancel", toCancel.Name, "command line");
                output.WriteLine("Cancellation requested.");
                return 0;
            case "drill":
                var toDrill = FindJob(a.Required(0, "job name"));
                Services.Runs.RequestDrill(toDrill.Id);
                Services.Audit.Add("job.drill", toDrill.Name, "command line");
                output.WriteLine("Restore drill queued; the service runs it within a few seconds.");
                return 0;
            case "export":
                return Export(a, output);
            case "import":
                return Import(a, output, resolvePlaceholders: false);
            case "apply":
                return Import(a, output, resolvePlaceholders: true);
            case "validate":
                return Validate(a, output);
            case "templates":
                foreach (var template in JobTemplate.All)
                {
                    output.WriteLine($"{template.Name}\n  {template.Description}\n");
                }

                return 0;
            default:
                throw new CliException($"Unknown command '{args[0]}'.");
        }
    }

    // ------------------------------------------------------------------ backups on disk

    private static async Task<int> VerifyAsync(Arguments a, TextWriter output)
    {
        var path = a.Required(0, "backup file");
        var target = Path.Combine(Path.GetTempPath(), "storix-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RestoreService.RestoreFromFileAsync(path, new RestoreRequest(target, Secret(a), VerifyChecksum: !a.Flag("no-verify")), new Progress<string>(output.WriteLine), CancellationToken.None);
            output.WriteLine($"OK: {result.Files.Count} file(s), {BackupJobRunner.FormatSize(result.TotalBytes)}{(result.ChecksumVerified ? ", SHA-256 verified" : ", no checksum file found")}.");
            return 0;
        }
        finally
        {
            TryDelete(target);
        }
    }

    private static async Task<int> ListFilesAsync(Arguments a, TextWriter output)
    {
        var path = a.Required(0, "backup file");
        var indexPath = path + BackupIndex.Extension;
        BackupIndex index;
        if (File.Exists(indexPath))
        {
            index = await BackupIndex.ReadAsync(indexPath, Secret(a), CancellationToken.None);
        }
        else
        {
            var target = Path.Combine(Path.GetTempPath(), "storix-list-" + Guid.NewGuid().ToString("N"));
            try
            {
                await RestoreService.RestoreFromFileAsync(path, new RestoreRequest(target, Secret(a)), null, CancellationToken.None);
                index = new BackupIndex
                {
                    Archive = Path.GetFileName(path),
                    Entries = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                        .Select(f => new IndexEntry { Path = Path.GetRelativePath(target, f).Replace('\\', '/'), Size = new FileInfo(f).Length, Modified = File.GetLastWriteTime(f) })
                        .ToList(),
                };
            }
            finally
            {
                TryDelete(target);
            }
        }

        foreach (var entry in index.Entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"{entry.Modified.ToLocalTime():yyyy-MM-dd HH:mm}  {entry.Size,14:N0}  {entry.Path}");
        }

        output.WriteLine($"{index.Entries.Count:N0} file(s), {BackupJobRunner.FormatSize(index.TotalSize)}");
        return 0;
    }

    private static async Task<int> RestoreAsync(Arguments a, TextWriter output)
    {
        var path = a.Required(0, "backup file");
        var to = a.Option("to") ?? throw new CliException("Missing --to <folder>.");
        var include = a.Options("include");
        var request = new RestoreRequest(to, Secret(a), a.Flag("overwrite"), !a.Flag("no-verify")) { Include = include.Count > 0 ? include : null };
        var result = await RestoreService.RestoreFromFileAsync(path, request, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"Restored {result.Files.Count} file(s) ({BackupJobRunner.FormatSize(result.TotalBytes)}) to {to}.");
        return 0;
    }

    // ------------------------------------------------------------------ jobs

    private static int Jobs(TextWriter output)
    {
        foreach (var job in Services.Jobs.GetAll())
        {
            var last = Services.Runs.GetLast(job.Id);
            DateTimeOffset? next = null;
            try
            {
                next = job.Enabled ? ScheduleCalculator.GetNextOccurrence(job.Schedule, DateTimeOffset.UtcNow) : null;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or TimeZoneNotFoundException)
            {
            }

            output.WriteLine($"{job.Name}");
            output.WriteLine($"  {(job.Enabled ? "enabled" : "disabled")}, {ScheduleCalculator.Describe(job.Schedule)}, next: {next?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-"}");
            output.WriteLine($"  last: {(last is null ? "never" : $"{last.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} {last.Status}")}");
        }

        return 0;
    }

    private static int History(Arguments a, TextWriter output)
    {
        Guid? jobId = a.Positional.Count > 0 ? FindJob(a.Positional[0]).Id : null;
        var limit = int.TryParse(a.Option("limit"), out var l) ? l : 20;
        foreach (var run in Services.Runs.GetRecent(jobId, limit))
        {
            output.WriteLine($"{run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {run.Status,-18} {run.Trigger,-12} {run.JobName}  {run.FileName}  {run.Message}");
        }

        return 0;
    }

    private static async Task<int> RunJobAsync(Arguments a, TextWriter output)
    {
        var job = FindJob(a.Required(0, "job name"));
        if (a.Flag("dry-run"))
        {
            output.WriteLine(await DryRun.RunAsync(job, new DestinationFactory(), CancellationToken.None));
            return 0;
        }

        var before = Services.Runs.GetLast(job.Id)?.Id;
        Services.Runs.RequestRun(job.Id);
        Services.Audit.Add("job.run", job.Name, "command line");
        output.WriteLine($"'{job.Name}' queued; the Storix service starts it within a few seconds.");
        if (!a.Flag("wait"))
        {
            return 0;
        }

        while (true)
        {
            await Task.Delay(2000);
            var last = Services.Runs.GetLast(job.Id);
            if (last is not null && last.Id != before && last.Status != RunStatus.Running)
            {
                output.WriteLine($"{last.Status}: {last.Message}");
                return last.Status == RunStatus.Succeeded ? 0 : 1;
            }
        }
    }

    private static int Export(Arguments a, TextWriter output)
    {
        var file = a.Required(0, "output file");
        var json = ConfigurationPorter.Export(Services.Jobs.GetAll(), a.Flag("settings") ? Services.Settings.Get() : null, a.Option("passphrase"));
        File.WriteAllText(file, json);
        Services.Audit.Add("config.export", file, "command line");
        output.WriteLine($"Exported to {file}.");
        return 0;
    }

    private static int Import(Arguments a, TextWriter output, bool resolvePlaceholders)
    {
        var package = ConfigurationPorter.Import(File.ReadAllText(a.Required(0, "file")), a.Option("passphrase"));
        if (resolvePlaceholders)
        {
            var missing = SecretPlaceholders.Resolve(package);
            if (missing.Count > 0)
            {
                throw new CliException($"Environment variables not set: {string.Join(", ", missing)}");
            }
        }

        var invalid = package.Jobs.Select(j => (j, BackupJobRunner.GetValidationErrors(j))).Where(x => x.Item2.Count > 0).ToList();
        foreach (var (job, errors) in invalid)
        {
            output.WriteLine($"warning: '{job.Name}': {string.Join(" ", errors)}");
        }

        foreach (var job in package.Jobs)
        {
            Services.Audit.Add("config.import", job.Name, NT.Storix.Core.Security.AuditDiff.Describe(Services.Jobs.Get(job.Id), job) + " (command line)");
            Services.Jobs.Save(job);
        }

        if (a.Flag("settings") && package.Settings is not null)
        {
            Services.Settings.Save(package.Settings);
        }

        output.WriteLine($"Imported {package.Jobs.Count} job(s).");
        return 0;
    }

    internal static int Validate(Arguments a, TextWriter output)
    {
        var package = ConfigurationPorter.Import(File.ReadAllText(a.Required(0, "file")), a.Option("passphrase"));
        var missing = SecretPlaceholders.Resolve(package);
        var failed = false;
        foreach (var job in package.Jobs)
        {
            var errors = BackupJobRunner.GetValidationErrors(job);
            output.WriteLine(errors.Count == 0 ? $"OK      {job.Name}" : $"INVALID {job.Name}: {string.Join(" ", errors)}");
            failed |= errors.Count > 0;
        }

        foreach (var name in missing)
        {
            output.WriteLine($"warning: environment variable {name} is not set.");
        }

        return failed ? 1 : 0;
    }

    // ------------------------------------------------------------------ helpers

    private static BackupJob FindJob(string nameOrId)
    {
        var jobs = Services.Jobs.GetAll();
        return jobs.FirstOrDefault(j => j.Id.ToString().Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
               ?? jobs.FirstOrDefault(j => j.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
               ?? throw new CliException($"Job '{nameOrId}' not found. Use 'storix jobs' to list jobs.");
    }

    private static string? Secret(Arguments a)
    {
        var password = a.Option("password") ?? (a.Option("password-env") is { } name ? Environment.GetEnvironmentVariable(name) : null);
        return EncryptionSecret.Combine(password, a.Option("key-file"));
    }

    private static string RequireSecret(Arguments a) =>
        Secret(a) ?? throw new CliException("Missing --password, --password-env or --key-file.");

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static class Services
    {
        private static readonly Lazy<StorixDatabase> Database = new(() => new StorixDatabase(StorixPaths.DatabasePath));
        private static readonly ISecretProtector Protector = new MachineSecretProtector();

        public static JobRepository Jobs => new(Database.Value, Protector);

        public static RunRepository Runs => new(Database.Value);

        public static SettingsRepository Settings => new(Database.Value, Protector);

        public static AuditRepository Audit => new(Database.Value);
    }
}
