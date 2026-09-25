using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Ipc;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

public class PipeTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    private static string UniqueName() => "storix-test-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Request_and_response_round_trip()
    {
        var name = UniqueName();
        using var stop = new CancellationTokenSource();
        var jobId = Guid.NewGuid();
        var server = StorixPipe.ServeAsync(
            (request, _) => Task.FromResult(request.Command == "status"
                ? new PipeResponse { Ok = true, Running = [new RunningJob(jobId, "db", DateTimeOffset.UtcNow, "Manual")] }
                : new PipeResponse { Error = $"echo {request.JobId}" }),
            NullLogger.Instance, stop.Token, name);

        var status = await StorixPipe.SendAsync(new PipeRequest { Command = "status" }, TimeSpan.FromSeconds(10), name);
        var other = await StorixPipe.SendAsync(new PipeRequest { Command = "x", JobId = jobId }, TimeSpan.FromSeconds(10), name);

        Assert.True(status!.Ok);
        Assert.Equal("db", Assert.Single(status.Running).JobName);
        Assert.Equal(StorixInfo.Version, status.Version);
        Assert.False(other!.Ok);
        Assert.Equal($"echo {jobId}", other.Error);

        stop.Cancel();
        await server;
    }

    [Fact]
    public async Task Without_a_service_requests_go_to_the_database_queue()
    {
        using var temp = new TempDirectory();
        var runs = new RunRepository(new StorixDatabase(temp.Combine("s.db")));
        var id = Guid.NewGuid();

        Assert.Null(await StorixPipe.SendAsync(new PipeRequest(), TimeSpan.FromMilliseconds(300), UniqueName()));
        var immediate = await ServiceRequests.SendAsync(runs, "run", id, UniqueName());

        Assert.False(immediate);
        Assert.Equal([id], runs.DequeueRunRequests());
    }

    [Fact]
    public async Task Wake_starts_a_requested_run_without_waiting_for_the_tick()
    {
        using var temp = new TempDirectory();
        var database = new StorixDatabase(temp.Combine("s.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        var jobs = new JobRepository(database, new PlainProtector());
        var runs = new RunRepository(database);
        temp.WriteFile("src/a.txt", "a");
        var job = new BackupJob
        {
            Name = "Now",
            Schedule = { Kind = ScheduleKind.Manual },
            Source = { Files = { Paths = [temp.Combine("src")] } },
            Destinations = [new DestinationDefinition { LocalFolder = { Path = temp.Combine("dst") } }],
        };
        jobs.Save(job);
        var runner = new BackupJobRunner(runs, settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
        var scheduler = new BackupScheduler(jobs, runs, settings, runner, Array.Empty<INotifier>(), NullLogger<BackupScheduler>.Instance);
        using var stop = new CancellationTokenSource();
        var loop = scheduler.RunAsync(stop.Token);
        await Task.Delay(500); // Let the first tick pass.

        var watch = System.Diagnostics.Stopwatch.StartNew();
        runs.RequestRun(job.Id);
        scheduler.Wake();
        while (runs.GetLast(job.Id)?.Status is not RunStatus.Succeeded)
        {
            Assert.True(watch.Elapsed < BackupScheduler.TickInterval, "The run waited for the regular tick.");
            await Task.Delay(50);
        }

        stop.Cancel();
        await loop;
    }
}
