using Microsoft.Data.Sqlite;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Persistence;

/// <summary>Backup history and the queue of manual run requests coming from the UI.</summary>
public sealed class RunRepository(StorixDatabase database)
{
    private const string Columns = "id, job_id, job_name, trigger, status, started_at, finished_at, file_name, size_bytes, sha256, message";

    public void Insert(BackupRun run)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runs (id, job_id, job_name, trigger, status, started_at)
            VALUES ($id, $job, $name, $trigger, $status, $started)
            """;
        command.Parameters.AddWithValue("$id", run.Id.ToString());
        command.Parameters.AddWithValue("$job", run.JobId.ToString());
        command.Parameters.AddWithValue("$name", run.JobName);
        command.Parameters.AddWithValue("$trigger", run.Trigger.ToString());
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$started", run.StartedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Complete(BackupRun run)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs SET status = $status, finished_at = $finished, file_name = $file, size_bytes = $size,
                            sha256 = $sha, message = $message, log = $log
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", run.Id.ToString());
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$finished", (object?)run.FinishedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$file", (object?)run.FileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", (object?)run.SizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha", (object?)run.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", (object?)run.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("$log", (object?)run.Log ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BackupRun> GetRecent(Guid? jobId = null, int limit = 200)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM runs {(jobId is null ? string.Empty : "WHERE job_id = $job")} ORDER BY started_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        if (jobId is not null)
        {
            command.Parameters.AddWithValue("$job", jobId.Value.ToString());
        }

        using var reader = command.ExecuteReader();
        var runs = new List<BackupRun>();
        while (reader.Read())
        {
            runs.Add(Read(reader));
        }

        return runs;
    }

    public BackupRun? GetLast(Guid jobId) => GetRecent(jobId, 1).FirstOrDefault();

    public string? GetLog(Guid runId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT log FROM runs WHERE id = $id";
        command.Parameters.AddWithValue("$id", runId.ToString());
        return command.ExecuteScalar() as string;
    }

    /// <summary>Crash recovery: marks runs left in the Running state by a previous process as Interrupted.</summary>
    public int MarkInterrupted()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs SET status = $interrupted, finished_at = $now,
                message = 'The service stopped while this backup was running.'
            WHERE status = $running
            """;
        command.Parameters.AddWithValue("$interrupted", RunStatus.Interrupted.ToString());
        command.Parameters.AddWithValue("$running", RunStatus.Running.ToString());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery();
    }

    public int DeleteOlderThan(DateTimeOffset threshold)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM runs WHERE started_at < $threshold AND status <> $running";
        command.Parameters.AddWithValue("$threshold", threshold.ToString("O"));
        command.Parameters.AddWithValue("$running", RunStatus.Running.ToString());
        return command.ExecuteNonQuery();
    }

    public void RequestRun(Guid jobId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO run_requests (job_id, requested_at) VALUES ($job, $now)";
        command.Parameters.AddWithValue("$job", jobId.ToString());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>Returns and removes all pending manual run requests.</summary>
    public IReadOnlyList<Guid> DequeueRunRequests()
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT job_id FROM run_requests";

        var ids = new List<Guid>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        command.CommandText = "DELETE FROM run_requests";
        command.ExecuteNonQuery();
        transaction.Commit();
        return ids;
    }

    private static BackupRun Read(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JobId = Guid.Parse(reader.GetString(1)),
        JobName = reader.GetString(2),
        Trigger = Enum.Parse<RunTrigger>(reader.GetString(3)),
        Status = Enum.Parse<RunStatus>(reader.GetString(4)),
        StartedAt = DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
        FinishedAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
        FileName = reader.IsDBNull(7) ? null : reader.GetString(7),
        SizeBytes = reader.IsDBNull(8) ? null : reader.GetInt64(8),
        Sha256 = reader.IsDBNull(9) ? null : reader.GetString(9),
        Message = reader.IsDBNull(10) ? null : reader.GetString(10),
    };
}
