using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NT.Storix.Core.Models;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Scheduling;

namespace NT.Storix.Core.Engine;

/// <summary>
/// Heart of the Windows service: polls the metadata database, starts due and manually requested jobs,
/// limits concurrency and never runs the same job twice at the same time.
/// </summary>
public sealed class BackupScheduler(
    JobRepository jobs,
    RunRepository runs,
    SettingsRepository settings,
    BackupJobRunner runner,
    ILogger<BackupScheduler> logger)
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private SemaphoreSlim _slots = new(1);

    public IReadOnlyCollection<Guid> RunningJobs => _running.Keys.ToList();

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var appSettings = settings.Get();
        _slots = new SemaphoreSlim(Math.Max(1, appSettings.MaxConcurrentJobs));
        Recover(appSettings);

        logger.LogInformation("Scheduler started (max {Concurrency} concurrent job(s)).", appSettings.MaxConcurrentJobs);

        var lastTick = DateTimeOffset.UtcNow;
        try
        {
            CatchUp(lastTick, stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Missed-run catch-up failed.");
        }

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            do
            {
                var now = DateTimeOffset.UtcNow;
                try
                {
                    Tick(lastTick, now, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Scheduler tick failed.");
                }

                lastTick = now;
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        var pending = _running.Values.ToArray();
        if (pending.Length > 0)
        {
            logger.LogInformation("Waiting for {Count} running job(s) to stop.", pending.Length);
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None));
        }

        logger.LogInformation("Scheduler stopped.");
    }

    /// <summary>Starts every job due in (<paramref name="fromUtc"/>, <paramref name="nowUtc"/>] and every requested job.</summary>
    internal void Tick(DateTimeOffset fromUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var requested = runs.DequeueRunRequests().ToHashSet();

        foreach (var job in jobs.GetAll())
        {
            if (requested.Contains(job.Id))
            {
                Start(job, RunTrigger.Manual, cancellationToken);
                continue;
            }

            if (!job.Enabled)
            {
                continue;
            }

            try
            {
                var next = ScheduleCalculator.GetNextOccurrence(job.Schedule, fromUtc);
                if (next is not null && next <= nowUtc)
                {
                    Start(job, RunTrigger.Schedule, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Invalid schedule for job {Job}.", job.Name);
            }
        }
    }

    private void Start(BackupJob job, RunTrigger trigger, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(job.Id, completion.Task))
        {
            logger.LogWarning("Job {Job} is still running; the {Trigger} run was skipped.", job.Name, trigger);
            return;
        }

        _ = Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await _slots.WaitAsync(cancellationToken);
                acquired = true;
                await runner.RunAsync(job, trigger, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error while running job {Job}.", job.Name);
            }
            finally
            {
                if (acquired)
                {
                    _slots.Release();
                }

                _running.TryRemove(job.Id, out _);
                completion.TrySetResult();
            }
        }, CancellationToken.None);
    }

    /// <summary>Starts once every enabled job whose scheduled run was missed while the service was not running.</summary>
    internal void CatchUp(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        foreach (var job in jobs.GetAll().Where(j => j.Enabled))
        {
            try
            {
                var last = runs.GetLast(job.Id);
                if (ScheduleCalculator.HasMissedRun(job.Schedule, last?.StartedAt, nowUtc))
                {
                    logger.LogInformation("Job {Job} missed a scheduled run since {LastRun}; running it now.", job.Name, last!.StartedAt);
                    Start(job, RunTrigger.Schedule, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not evaluate missed runs for job {Job}.", job.Name);
            }
        }
    }

    /// <summary>Crash recovery: closes runs left open by a previous process and removes their temporary files.</summary>
    internal void Recover(AppSettings appSettings)
    {
        var interrupted = runs.MarkInterrupted();
        if (interrupted > 0)
        {
            logger.LogWarning("Marked {Count} unfinished run(s) from a previous session as interrupted.", interrupted);
        }

        var staging = BackupJobRunner.GetStagingRoot(appSettings);
        if (Directory.Exists(staging))
        {
            foreach (var directory in Directory.EnumerateDirectories(staging))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not delete stale staging folder {Folder}.", directory);
                }
            }
        }

        if (appSettings.HistoryRetentionDays > 0)
        {
            runs.DeleteOlderThan(DateTimeOffset.UtcNow.AddDays(-appSettings.HistoryRetentionDays));
        }
    }
}
