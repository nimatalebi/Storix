using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Monitoring;

public sealed record JobStats(
    BackupJob Job,
    int Runs,
    int Succeeded,
    int Failed,
    long? LatestSize,
    long EstimatedStored,
    double? GrowthPerDay,
    long? ForecastSize)
{
    /// <summary>Share of finished backups that succeeded (partial counts as success), or null without runs.</summary>
    public double? SuccessRate => Succeeded + Failed == 0 ? null : (double)Succeeded / (Succeeded + Failed);
}

public sealed record DestinationStats(string Destination, int Jobs, long EstimatedStored);

public sealed record Dashboard(IReadOnlyList<JobStats> Jobs, IReadOnlyList<DestinationStats> Destinations)
{
    public long TotalStored => Destinations.Sum(d => d.EstimatedStored);

    public double? SuccessRate
    {
        get
        {
            var succeeded = Jobs.Sum(j => j.Succeeded);
            var total = succeeded + Jobs.Sum(j => j.Failed);
            return total == 0 ? null : (double)succeeded / total;
        }
    }
}

/// <summary>
/// Figures for the dashboard, computed from the run history only (no destination is contacted):
/// success rate, the space the backups kept by the retention policy take, the growth trend of the
/// backup size and a linear forecast.
/// </summary>
public static class DashboardStats
{
    public static Dashboard Compute(IReadOnlyList<BackupJob> jobs, IReadOnlyList<BackupRun> runs, DateTimeOffset now, int periodDays = 30, int forecastDays = 30)
    {
        var from = now.AddDays(-periodDays);
        var jobStats = new List<JobStats>();
        var perDestination = new Dictionary<string, (int Jobs, long Bytes)>(StringComparer.OrdinalIgnoreCase);

        foreach (var job in jobs)
        {
            var backups = runs.Where(r => r.JobId == job.Id && r.Trigger != RunTrigger.RestoreDrill).ToList();
            var recent = backups.Where(r => r.StartedAt >= from).ToList();
            var succeeded = recent.Count(IsSuccess);
            var failed = recent.Count(r => r.Status is RunStatus.Failed or RunStatus.Interrupted);

            var sized = backups.Where(r => IsSuccess(r) && r.SizeBytes is > 0).OrderBy(r => r.StartedAt).ToList();
            var stored = EstimateStored(sized, job.Retention, now);
            var slope = Slope(sized.Where(r => r.StartedAt >= from).ToList());
            var latest = sized.LastOrDefault()?.SizeBytes;
            long? forecast = latest is { } size && slope is { } perDay ? Math.Max(0, (long)(size + perDay * forecastDays)) : latest;

            jobStats.Add(new JobStats(job, recent.Count, succeeded, failed, latest, stored, slope, forecast));

            foreach (var destination in job.Destinations.Where(d => d.Enabled))
            {
                var key = destination.Name;
                perDestination.TryGetValue(key, out var entry);
                perDestination[key] = (entry.Jobs + 1, entry.Bytes + stored);
            }
        }

        var destinations = perDestination
            .Select(p => new DestinationStats(p.Key, p.Value.Jobs, p.Value.Bytes))
            .OrderByDescending(d => d.EstimatedStored)
            .ToList();
        return new Dashboard(jobStats, destinations);
    }

    private static bool IsSuccess(BackupRun run) => run.Status is RunStatus.Succeeded or RunStatus.PartiallySucceeded;

    /// <summary>Sum of the sizes of the successful backups the retention policy keeps.</summary>
    internal static long EstimateStored(IReadOnlyList<BackupRun> successful, RetentionPolicy policy, DateTimeOffset now)
    {
        var files = successful.Select(r => (Run: r, File: new BackupFileInfo(r.Id.ToString("N"), r.StartedAt, []))).ToList();
        var deleted = RetentionPlanner.SelectForDeletion(files.Select(f => f.File), policy, now).ToHashSet();
        return files.Where(f => !deleted.Contains(f.File)).Sum(f => f.Run.SizeBytes ?? 0);
    }

    /// <summary>Least-squares slope of backup size over time, in bytes per day; null with fewer than two points.</summary>
    internal static double? Slope(IReadOnlyList<BackupRun> sized)
    {
        if (sized.Count < 2)
        {
            return null;
        }

        var origin = sized[0].StartedAt;
        var xs = sized.Select(r => (r.StartedAt - origin).TotalDays).ToList();
        var ys = sized.Select(r => (double)r.SizeBytes!.Value).ToList();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var denominator = xs.Sum(x => (x - meanX) * (x - meanX));
        if (denominator < 1e-9)
        {
            return null;
        }

        return xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum() / denominator;
    }
}
