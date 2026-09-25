using System.Text.Json;
using NT.Storix.Core.Models;
using NT.Storix.Core.Security;

namespace NT.Storix.Core.Persistence;

/// <summary>Stores job definitions as JSON. Secrets are encrypted with the <see cref="ISecretProtector"/>.</summary>
public sealed class JobRepository(StorixDatabase database, ISecretProtector protector)
{
    public IReadOnlyList<BackupJob> GetAll()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT definition FROM jobs ORDER BY name COLLATE NOCASE";
        using var reader = command.ExecuteReader();

        var jobs = new List<BackupJob>();
        while (reader.Read())
        {
            jobs.Add(Deserialize(reader.GetString(0)));
        }

        return jobs;
    }

    public BackupJob? Get(Guid id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT definition FROM jobs WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteScalar() is string json ? Deserialize(json) : null;
    }

    public void Save(BackupJob job)
    {
        var copy = StorixJson.Clone(job);
        SecretWalker.Transform(copy, protector.Protect);

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (id, name, enabled, definition, updated_at)
            VALUES ($id, $name, $enabled, $definition, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, enabled = excluded.enabled,
                definition = excluded.definition, updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$name", job.Name);
        command.Parameters.AddWithValue("$enabled", job.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(copy, StorixJson.Options));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Delete(Guid id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM jobs WHERE id = $id; DELETE FROM run_requests WHERE job_id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>A cheap value that changes whenever any job is added, changed or removed.</summary>
    public string GetVersionStamp()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) || '|' || IFNULL(MAX(updated_at), '') FROM jobs";
        return (string)command.ExecuteScalar()!;
    }

    private BackupJob Deserialize(string json)
    {
        var job = JsonSerializer.Deserialize<BackupJob>(json, StorixJson.Options)!;
        SecretWalker.Transform(job, protector.Unprotect);
        return job;
    }
}
