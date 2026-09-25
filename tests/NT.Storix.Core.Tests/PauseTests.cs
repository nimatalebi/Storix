using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

public class PauseTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    [Fact]
    public async Task Paused_run_waits_until_resumed()
    {
        BackupJobRunner.PausePollInterval = TimeSpan.FromMilliseconds(100);
        using var temp = new TempDirectory();
        var database = new StorixDatabase(temp.Combine("s.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        var runs = new RunRepository(database);
        var runner = new BackupJobRunner(runs, settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
        temp.WriteFile("src/a.txt", "a");
        var job = new BackupJob
        {
            Name = "Pause",
            Source = { Files = { Paths = [temp.Combine("src")] } },
            Destinations = [new DestinationDefinition { LocalFolder = { Path = temp.Combine("dst") } }],
        };

        runs.SetPaused(job.Id, true);
        Assert.True(runs.IsPaused(job.Id));
        var task = runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);

        await Task.Delay(1000);
        Assert.False(task.IsCompleted);
        Assert.Equal(RunStatus.Running, runs.GetLast(job.Id)!.Status);

        runs.SetPaused(job.Id, false);
        var run = await task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Contains("paused from the manager", run.Log);
        Assert.Contains("Resumed.", run.Log);
    }

    [Theory]
    [InlineData(0x1u, false)]       // Unrestricted
    [InlineData(0x2u, true)]        // Fixed (capped plan)
    [InlineData(0x4u, true)]        // Variable (pay per byte)
    [InlineData(0x40001u, true)]    // Roaming
    public void Metered_cost_flags(uint cost, bool metered)
    {
        Assert.Equal(metered, MeteredConnection.IsMeteredCost(cost));
    }

    [Fact]
    public void Metered_detection_never_throws()
    {
        _ = MeteredConnection.IsMetered();
    }
}
