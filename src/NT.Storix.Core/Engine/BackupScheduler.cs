using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
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
    IEnumerable<INotifier> notifiers,
    ILogger<BackupScheduler> logger,
    RestoreDrillRunner? drills = null)
{
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMinutes(10);
    private DateTimeOffset _lastHealthCheck = DateTimeOffset.MinValue;

    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellation = new();
    private SemaphoreSlim _slots = new(1);

    public IReadOnlyCollection<Guid> RunningJobs => _running.Keys.ToList();

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var appSettings = await PrepareAsync();

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
                    if (now - _lastHealthCheck >= HealthCheckInterval)
                    {
                        _lastHealthCheck = now;
                        await CheckStaleJobsAsync(now);
                    }
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

    /// <summary>Loads settings, sizes the concurrency limit and performs crash recovery.</summary>
    internal Task<AppSettings> PrepareAsync()
    {
        var appSettings = settings.Get();
        _slots = new SemaphoreSlim(Math.Max(1, appSettings.MaxConcurrentJobs));
        Recover(appSettings);
        return Task.FromResult(appSettings);
    }

    /// <summary>Starts every job due in (<paramref name="fromUtc"/>, <paramref name="nowUtc"/>] and every requested job.</summary>
    internal void Tick(DateTimeOffset fromUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        foreach (var jobId in runs.DequeueCancelRequests())
        {
            if (_cancellation.TryGetValue(jobId, out var source))
            {
                logger.LogInformation("Cancelling the running backup of job {JobId} on request.", jobId);
                source.Cancel();
            }
        }

        var requested = runs.DequeueRunRequests().ToHashSet();
        var drillRequested = drills is null ? [] : runs.DequeueDrillRequests().ToHashSet();

        foreach (var job in jobs.GetAll())
        {
            if (requested.Contains(job.Id))
            {
                Start(job, RunTrigger.Manual, cancellationToken);
                continue;
            }

            if (drillRequested.Contains(job.Id) || (job.Enabled && IsDrillDue(job, nowUtc)))
            {
                StartDrill(job, cancellationToken);
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

    private void Start(BackupJob job, RunTrigger trigger, CancellationToken cancellationToken) =>
        Start(job, trigger.ToString(), ct => runner.RunAsync(job, trigger, ct), cancellationToken);

    private void StartDrill(BackupJob job, CancellationToken cancellationToken)
    {
        if (drills is not null)
        {
            Start(job, "restore drill", ct => drills.RunAsync(job, ct), cancellationToken);
        }
    }

    /// <summary>A drill is due when drills are enabled, the job has a successful backup and the last drill is old enough.</summary>
    internal bool IsDrillDue(BackupJob job, DateTimeOffset nowUtc)
    {
        if (drills is null || !job.RestoreDrill.Enabled)
        {
            return false;
        }

        var last = runs.GetLastDrill(job.Id);
        if (last is not null && nowUtc - last.Value < TimeSpan.FromDays(Math.Max(1, job.RestoreDrill.EveryDays)))
        {
            return false;
        }

        return runs.GetHealth(job.Id).LastSuccess is not null;
    }

    private void Start(BackupJob job, string trigger, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(job.Id, completion.Task))
        {
            logger.LogWarning("Job {Job} is still running; the {Trigger} run was skipped.", job.Name, trigger);
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellation[job.Id] = cancellation;

        _ = Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await _slots.WaitAsync(cancellation.Token);
                acquired = true;
                await work(cancellation.Token);
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
                _cancellation.TryRemove(job.Id, out _);
                cancellation.Dispose();
                completion.TrySetResult();
            }
        }, CancellationToken.None);
    }

    /// <summary>Dead man's switch: alerts for enabled jobs without a recent successful backup.</summary>
    internal async Task<int> CheckStaleJobsAsync(DateTimeOffset nowUtc)
    {
        var alerts = 0;
        foreach (var job in jobs.GetAll().Where(j => j.Enabled && j.Notifications.AlertIfNoSuccessForHours > 0))
        {
            var (lastSuccess, firstRun) = runs.GetHealth(job.Id);
            var key = $"stale-alert:{job.Id}";
            DateTimeOffset? lastAlert = settings.GetValue(key) is { } stored ? DateTimeOffset.Parse(stored, System.Globalization.CultureInfo.InvariantCulture) : null;

            if (DeadMansSwitch.ShouldAlert(job.Notifications.AlertIfNoSuccessForHours, lastSuccess, firstRun, lastAlert, nowUtc))
            {
                logger.LogWarning("Job {Job} has no successful backup within {Hours} hours.", job.Name, job.Notifications.AlertIfNoSuccessForHours);
                settings.SetValue(key, nowUtc.ToString("O"));
                await BackupJobRunner.NotifyAsync(notifiers, Notification.ForStale(job, lastSuccess), logger);
                alerts++;
            }
        }

        return alerts;
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
