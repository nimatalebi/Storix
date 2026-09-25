using NT.Storix.Cli;
using NT.Storix.Core.Configuration;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Tests;

public class CliTests
{
    private static async Task<(int Code, string Output)> Run(params string[] args)
    {
        var output = new StringWriter();
        var code = await Commands.RunAsync(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public async Task Standalone_verify_list_and_restore_need_only_the_backup_and_password()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("source/docs/a.txt", "alpha");
        temp.WriteFile("source/docs/b.txt", "beta");
        var (_, run) = await RestoreTests.BackupAsync(temp, encrypt: true);
        var backup = temp.Combine("target", run.FileName!);

        var verify = await Run("verify", backup, "--password", "pw");
        Assert.Equal(0, verify.Code);
        Assert.Contains("SHA-256 verified", verify.Output);

        var list = await Run("list-files", backup, "--password", "pw");
        Assert.Contains("source/docs/a.txt", list.Output);
        Assert.Contains("2 file(s)", list.Output);

        Environment.SetEnvironmentVariable("STORIX_TEST_PW", "pw");
        var restore = await Run("restore", backup, "--to", temp.Combine("out"), "--password-env", "STORIX_TEST_PW", "--include", "source/docs/b.txt");
        Assert.Equal(0, restore.Code);
        Assert.Equal("beta", File.ReadAllText(temp.Combine("out", "source", "docs", "b.txt")));
        Assert.False(File.Exists(temp.Combine("out", "source", "docs", "a.txt")));

        await Assert.ThrowsAnyAsync<Exception>(() => Run("verify", backup, "--password", "wrong"));
    }

    [Fact]
    public async Task Validate_reports_invalid_jobs_and_resolves_placeholders()
    {
        using var temp = new TempDirectory();
        var good = new BackupJob
        {
            Name = "Good",
            Source = { Files = { Paths = ["C:\\data"] } },
            Processing = { Encrypt = true, EncryptionPassword = "${env:STORIX_TEST_ARCHIVE_PW}" },
            Destinations = [new DestinationDefinition { LocalFolder = { Path = "D:\\backup" } }],
        };
        var bad = new BackupJob { Name = "Bad" };

        // Secrets are stripped by a plain export, so build the job file with placeholders kept.
        var file = temp.Combine("jobs.storix.json");
        var package = ConfigurationPorter.Import(ConfigurationPorter.Export([good, bad], null, "x"), "x");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(package, StorixJson.Indented));

        var result = await Run("validate", file);
        Assert.Equal(1, result.Code);
        Assert.Contains("OK      Good", result.Output);
        Assert.Contains("INVALID Bad", result.Output);
        Assert.Contains("STORIX_TEST_ARCHIVE_PW is not set", result.Output);

        Environment.SetEnvironmentVariable("STORIX_TEST_ARCHIVE_PW", "from-env");
        var resolved = SecretPlaceholders.Resolve(package);
        Assert.Empty(resolved);
        Assert.Equal("from-env", package.Jobs[0].Processing.EncryptionPassword);
    }

    [Fact]
    public async Task Help_version_and_unknown_command()
    {
        Assert.Contains("storix restore", (await Run("help")).Output);
        Assert.StartsWith("storix ", (await Run("version")).Output);
        await Assert.ThrowsAsync<CliException>(() => Run("frobnicate"));
    }

    [Fact]
    public void Templates_produce_valid_shapes()
    {
        foreach (var template in JobTemplate.All)
        {
            var job = template.Create();
            Assert.False(string.IsNullOrWhiteSpace(job.Name));
            Assert.NotEmpty(job.Destinations);
        }
    }

    [Fact]
    public async Task Dry_run_reports_files_and_destinations_without_writing()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("src/a.txt", "a");
        var job = new BackupJob
        {
            Name = "Dry",
            Source = { Files = { Paths = [temp.Combine("src")] } },
            Destinations = [new DestinationDefinition { Name = "Local", LocalFolder = { Path = temp.Combine("dst") } }],
        };

        var report = await Engine.DryRun.RunAsync(job, new Destinations.DestinationFactory(), CancellationToken.None);

        Assert.Contains("1 file(s)", report);
        Assert.Contains("reachable", report);
        Assert.Empty(Directory.GetFiles(temp.Combine("dst")));
        Assert.DoesNotContain(BackupIndex.Extension, report);
    }
}
