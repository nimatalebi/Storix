using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

public class ChainTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Chained_job_runs_after_the_first_one_succeeds()
    {
        using var temp = new TempDirectory();
        var database = new StorixDatabase(temp.Combine("s.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging"), MaxConcurrentJobs = 1 });
        var runs = new RunRepository(database);
        var jobs = new JobRepository(database, new PlainProtector());
        temp.WriteFile("src/a.txt", "a");

        BackupJob Job(string name) => new()
        {
            Name = name,
            Schedule = { Kind = ScheduleKind.Manual },
            Source = { Files = { Paths = [temp.Combine("src")] } },
            Destinations = [new DestinationDefinition { LocalFolder = { Path = temp.Combine("dst") } }],
        };

        var first = Job("first");
        var second = Job("second");
        second.RunAfterJobId = first.Id;
        jobs.Save(first);
        jobs.Save(second);

        var runner = new BackupJobRunner(runs, settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
        var scheduler = new BackupScheduler(jobs, runs, settings, runner, Array.Empty<INotifier>(), NullLogger<BackupScheduler>.Instance);
        await scheduler.PrepareAsync();

        runs.RequestRun(first.Id);
        scheduler.Tick(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (runs.GetLast(second.Id)?.Status is not RunStatus.Succeeded)
        {
            Assert.True(DateTime.UtcNow < deadline, "The chained job did not run.");
            await Task.Delay(100);
        }

        Assert.Equal(RunTrigger.Chain, runs.GetLast(second.Id)!.Trigger);
    }

    [Fact]
    public void Circuit_opens_after_repeated_failures_and_closes_after_cooldown()
    {
        var time = new ManualTime();
        var breaker = new CircuitBreaker(3, TimeSpan.FromMinutes(30), time);
        var destination = Guid.NewGuid();

        breaker.RecordFailure(destination);
        breaker.RecordFailure(destination);
        Assert.Null(breaker.OpenUntil(destination));

        breaker.RecordFailure(destination);
        Assert.Equal(time.Now.AddMinutes(30), breaker.OpenUntil(destination));

        time.Now = time.Now.AddMinutes(31);
        Assert.Null(breaker.OpenUntil(destination));

        breaker.RecordFailure(destination);
        breaker.RecordSuccess(destination);
        breaker.RecordFailure(destination);
        breaker.RecordFailure(destination);
        Assert.Null(breaker.OpenUntil(destination)); // Success reset the counter.
    }
}
