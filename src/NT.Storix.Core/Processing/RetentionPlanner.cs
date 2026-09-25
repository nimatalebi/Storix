using System.Globalization;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Processing;

/// <summary>Decides which existing backups must be deleted according to a <see cref="RetentionPolicy"/>.</summary>
public static class RetentionPlanner
{
    /// <summary>
    /// Returns the backups to delete. The newest backup is never deleted. A backup is deleted when "keep last"
    /// or "keep days" says so, unless a GFS rule (daily/weekly/monthly/yearly) protects it or a kept incremental
    /// backup depends on it.
    /// </summary>
    /// <param name="zone">Time zone used to group backups into days, weeks, months and years (default: local).</param>
    public static IReadOnlyList<BackupFileInfo> SelectForDeletion(IEnumerable<BackupFileInfo> backups, RetentionPolicy policy, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var ordered = backups.OrderByDescending(b => b.CreatedAt).ToList();
        var protectedByGfs = ProtectedByGfs(ordered, policy, zone ?? TimeZoneInfo.Local);
        var result = new List<BackupFileInfo>();

        for (var i = 1; i < ordered.Count; i++)
        {
            var tooMany = policy.KeepLast > 0 && i >= policy.KeepLast;
            var tooOld = policy.KeepDays > 0 && ordered[i].CreatedAt < now.AddDays(-policy.KeepDays);

            // With only GFS rules configured, everything not protected by them is removed.
            var gfsOnly = policy.UsesGfs && policy.KeepLast <= 0 && policy.KeepDays <= 0;

            if ((tooMany || tooOld || gfsOnly) && !protectedByGfs.Contains(ordered[i]))
            {
                result.Add(ordered[i]);
            }
        }

        // Incremental backups need every backup back to their full one: never break a chain that is kept.
        var keep = ordered.Except(result).ToList();
        foreach (var kept in keep.Where(b => BackupNaming.IsIncremental(b.Name)))
        {
            IReadOnlyList<BackupFileInfo> chain;
            try
            {
                chain = BackupNaming.ChainOf(ordered, kept.Name);
            }
            catch (InvalidDataException)
            {
                continue; // Its full backup is already gone: nothing to protect.
            }

            foreach (var needed in chain)
            {
                result.Remove(needed);
            }
        }

        return result;
    }

    internal static HashSet<BackupFileInfo> ProtectedByGfs(IReadOnlyList<BackupFileInfo> newestFirst, RetentionPolicy policy, TimeZoneInfo zone)
    {
        var keep = new HashSet<BackupFileInfo>();
        Protect(newestFirst, policy.KeepDaily, d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), zone, keep);
        Protect(newestFirst, policy.KeepWeekly, d => $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}", zone, keep);
        Protect(newestFirst, policy.KeepMonthly, d => d.ToString("yyyy-MM", CultureInfo.InvariantCulture), zone, keep);
        Protect(newestFirst, policy.KeepYearly, d => d.Year.ToString(CultureInfo.InvariantCulture), zone, keep);
        return keep;
    }

    private static void Protect(IReadOnlyList<BackupFileInfo> newestFirst, int count, Func<DateTime, string> bucket, TimeZoneInfo zone, HashSet<BackupFileInfo> keep)
    {
        if (count <= 0)
        {
            return;
        }

        var seen = new HashSet<string>();
        foreach (var backup in newestFirst)
        {
            // The newest backup of each bucket is the first one met in newest-first order.
            if (seen.Add(bucket(TimeZoneInfo.ConvertTime(backup.CreatedAt, zone).DateTime)))
            {
                keep.Add(backup);
                if (seen.Count == count)
                {
                    return;
                }
            }
        }
    }
}
