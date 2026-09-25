using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

public class HookAndControlTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    private static readonly bool Windows = OperatingSystem.IsWindows();

    private static string WriteEnv(string variable, string file) =>
        Windows ? $"echo %{variable}%> \"{file}\"" : $"printf '%s' \"${variable}\" > '{file}'";

    private static string Sleep(int seconds) => Windows ? $"ping -n {seconds + 1} 127.0.0.1 > nul" : $"sleep {seconds}";

    private static string Fail(int code) => $"exit {code}";

    private sealed class Setup
    {
        public Setup(TempDirectory temp)
        {
            var database = new StorixDatabase(temp.Combine("s.db"));
            Settings = new SettingsRepository(database, new PlainProtector());
            Settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
            Runs = new RunRepository(database);
            Jobs = new JobRepository(database, new PlainProtector());
            Runner = new BackupJobRunner(Runs, Settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
            temp.WriteFile("src/a.txt", "a");
            Job = new BackupJob
            {
                Name = "Hooks",
                Source = { Files = { Paths = [temp.Combine("src")] } },
                Destinations = [new DestinationDefinition { LocalFolder = { Path = temp.Combine("dst") } }],
            };
        }

        public SettingsRepository Settings { get; }

        public RunRepository Runs { get; }

        public JobRepository Jobs { get; }

        public BackupJobRunner Runner { get; }

        public BackupJob Job { get; }
    }

    [Fact]
    public async Task Pre_and_post_commands_run_with_environment()
    {
        using var temp = new TempDirectory();
        var setup = new Setup(temp);
        setup.Job.Hooks.PreCommand = WriteEnv("STORIX_JOB_NAME", temp.Combine("pre.txt"));
        setup.Job.Hooks.PostCommand = WriteEnv("STORIX_STATUS", temp.Combine("post.txt"));

        var run = await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal("Hooks", File.ReadAllText(temp.Combine("pre.txt")).Trim());
        Assert.Equal("Succeeded", File.ReadAllText(temp.Combine("post.txt")).Trim());
        Assert.Contains("Pre-command finished with exit code 0", run.Log);
    }

    [Fact]
    public async Task Failing_pre_command_aborts_but_post_command_still_runs()
    {
        using var temp = new TempDirectory();
        var setup = new Setup(temp);
        setup.Job.Hooks.PreCommand = Fail(3);
        setup.Job.Hooks.PostCommand = WriteEnv("STORIX_STATUS", temp.Combine("post.txt"));

        var run = await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("exit code 3", run.Message);
        Assert.Equal("Failed", File.ReadAllText(temp.Combine("post.txt")).Trim());
        Assert.False(Directory.Exists(temp.Combine("dst")) && Directory.EnumerateFiles(temp.Combine("dst")).Any());
    }

    [Fact]
    public async Task Failing_pre_command_can_be_ignored()
    {
        using var temp = new TempDirectory();
        var setup = new Setup(temp);
        setup.Job.Hooks.PreCommand = Fail(1);
        setup.Job.Hooks.AbortOnPreCommandFailure = false;

        var run = await setup.Runner.RunAsync(setup.Job, RunTrigger.Manual, CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, run.Status);
    }

    [Fact]
    public async Task Hook_timeout_kills_the_command()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await HookRunner.RunAsync(Sleep(30), new Dictionary<string, string?>(), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Free_space_estimate_and_check()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("f.bin", new string('x', 1000));
        var entries = new[] { new ArchiveEntry(file, "f.bin") };

        Assert.Equal(1000, FreeSpace.EstimateStagingBytes(entries, null, encrypt: false));
        Assert.Equal(2000, FreeSpace.EstimateStagingBytes(entries, null, encrypt: true));
        Assert.Equal(1250, FreeSpace.EstimateStagingBytes(entries, 1000, encrypt: false));

        FreeSpace.Ensure(temp.Path, 1, "test");
        var ex = Assert.Throws<IOException>(() => FreeSpace.Ensure(temp.Path, long.MaxValue / 4, "the staging folder"));
        Assert.Contains("Not enough free space", ex.Message);
    }

    [Fact]
    public async Task Running_job_can_be_cancelled_from_the_ui()
    {
        using var temp = new TempDirectory();
        var setup = new Setup(temp);
        setup.Job.Hooks.PreCommand = Sleep(20);
        setup.Jobs.Save(setup.Job);

        var scheduler = new BackupScheduler(setup.Jobs, setup.Runs, setup.Settings, setup.Runner, Array.Empty<INotifier>(), NullLogger<BackupScheduler>.Instance);
        await scheduler.PrepareAsync();

        setup.Runs.RequestRun(setup.Job.Id);
        scheduler.Tick(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CancellationToken.None);
        await WaitUntilAsync(() => setup.Runs.GetLast(setup.Job.Id)?.Status == RunStatus.Running);

        setup.Runs.RequestCancel(setup.Job.Id);
        scheduler.Tick(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CancellationToken.None);
        await WaitUntilAsync(() => setup.Runs.GetLast(setup.Job.Id)?.Status == RunStatus.Cancelled);
        await WaitUntilAsync(() => scheduler.RunningJobs.Count == 0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(100);
        }
    }
}
