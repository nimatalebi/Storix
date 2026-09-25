using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Processing;

/// <summary>Decides which existing backups must be deleted according to a <see cref="RetentionPolicy"/>.</summary>
public static class RetentionPlanner
{
    /// <summary>
    /// Returns the backups to delete. The newest backup is never deleted.
    /// </summary>
    public static IReadOnlyList<BackupFileInfo> SelectForDeletion(IEnumerable<BackupFileInfo> backups, RetentionPolicy policy, DateTimeOffset now)
    {
        var ordered = backups.OrderByDescending(b => b.CreatedAt).ToList();
        var result = new List<BackupFileInfo>();

        for (var i = 1; i < ordered.Count; i++)
        {
            var tooMany = policy.KeepLast > 0 && i >= policy.KeepLast;
            var tooOld = policy.KeepDays > 0 && ordered[i].CreatedAt < now.AddDays(-policy.KeepDays);
            if (tooMany || tooOld)
            {
                result.Add(ordered[i]);
            }
        }

        return result;
    }
}
