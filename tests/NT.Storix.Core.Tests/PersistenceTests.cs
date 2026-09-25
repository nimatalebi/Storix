using NT.Storix.Core.Models;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;

namespace NT.Storix.Core.Tests;

public class PersistenceTests
{
    private sealed class ReversingProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText is null ? null : "rev:" + new string(plainText.Reverse().ToArray());

        public string? Unprotect(string? protectedText) =>
            protectedText is not null && protectedText.StartsWith("rev:") ? new string(protectedText[4..].Reverse().ToArray()) : protectedText;
    }

    [Fact]
    public void Jobs_are_stored_with_protected_secrets()
    {
        using var temp = new TempDirectory();
        var database = new StorixDatabase(temp.Combine("storix.db"));
        var jobs = new JobRepository(database, new ReversingProtector());

        var job = new BackupJob { Name = "Files", Processing = { Encrypt = true, EncryptionPassword = "top-secret" } };
        jobs.Save(job);

        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT definition FROM jobs";
            var raw = (string)command.ExecuteScalar()!;
            Assert.DoesNotContain("top-secret", raw);
        }

        var loaded = jobs.Get(job.Id)!;
        Assert.Equal("top-secret", loaded.Processing.EncryptionPassword);
        Assert.Equal("top-secret", job.Processing.EncryptionPassword); // Original instance untouched.

        jobs.Delete(job.Id);
        Assert.Empty(jobs.GetAll());
    }

    [Fact]
    public void Runs_history_requests_and_crash_recovery()
    {
        using var temp = new TempDirectory();
        var runs = new RunRepository(new StorixDatabase(temp.Combine("storix.db")));
        var jobId = Guid.NewGuid();

        var finished = new BackupRun { JobId = jobId, JobName = "a", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        runs.Insert(finished);
        finished.Status = RunStatus.Succeeded;
        finished.FinishedAt = DateTimeOffset.UtcNow;
        finished.SizeBytes = 42;
        finished.Log = "log text";
        runs.Complete(finished);

        runs.Insert(new BackupRun { JobId = jobId, JobName = "a" });
        Assert.Equal(1, runs.MarkInterrupted());

        var recent = runs.GetRecent(jobId);
        Assert.Equal(2, recent.Count);
        Assert.Equal(RunStatus.Interrupted, recent[0].Status);
        Assert.Equal(42, recent[1].SizeBytes);
        Assert.Equal("log text", runs.GetLog(finished.Id));

        runs.RequestRun(jobId);
        runs.RequestRun(jobId);
        Assert.Equal([jobId], runs.DequeueRunRequests());
        Assert.Empty(runs.DequeueRunRequests());
    }
}
