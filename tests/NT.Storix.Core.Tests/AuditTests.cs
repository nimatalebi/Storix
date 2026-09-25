using NT.Storix.Core.Models;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;

namespace NT.Storix.Core.Tests;

public class AuditTests
{
    [Fact]
    public void Diff_lists_changes_and_never_shows_secrets()
    {
        var before = new BackupJob { Name = "Nightly", Processing = { Encrypt = true, EncryptionPassword = "old-secret" }, Retention = { KeepLast = 7 } };
        var after = StorixJson.Clone(before);
        after.Retention.KeepLast = 14;
        after.Processing.EncryptionPassword = "new-secret";

        var diff = AuditDiff.Describe(before, after);

        Assert.Contains("retention.keepLast: 7 -> 14", diff);
        Assert.Contains("processing.encryptionPassword", diff);
        Assert.DoesNotContain("old-secret", diff);
        Assert.DoesNotContain("new-secret", diff);
        Assert.Equal("no changes", AuditDiff.Describe(before, StorixJson.Clone(before)));
        Assert.Contains("(none) ->", AuditDiff.Describe<BackupJob>(null, after));
    }

    [Fact]
    public void Audit_entries_are_stored_newest_first()
    {
        using var temp = new TempDirectory();
        var audit = new AuditRepository(new StorixDatabase(temp.Combine("a.db")));
        audit.Add("job.create", "Nightly", "created");
        audit.Add("restore", "Nightly", "to D:\\restore", user: "CONTOSO\\alice");

        var entries = audit.GetRecent();
        Assert.Equal(["restore", "job.create"], entries.Select(e => e.Action));
        Assert.Equal("CONTOSO\\alice", entries[0].User);
        Assert.Equal(Environment.MachineName, entries[0].Machine);
    }
}
