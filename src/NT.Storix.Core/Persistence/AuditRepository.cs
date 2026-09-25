using System.Globalization;

namespace NT.Storix.Core.Persistence;

public sealed record AuditEntry(DateTimeOffset At, string User, string Machine, string Action, string Target, string? Details);

/// <summary>Who changed what and when (configuration changes, restores, service control).</summary>
public sealed class AuditRepository(StorixDatabase database)
{
    public static string CurrentUser => $"{Environment.UserDomainName}\\{Environment.UserName}";

    public void Add(string action, string target, string? details = null, string? user = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO audit_log (at, user_name, machine, action, target, details) VALUES ($at, $user, $machine, $action, $target, $details)";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$user", user ?? CurrentUser);
        command.Parameters.AddWithValue("$machine", Environment.MachineName);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditEntry> GetRecent(int limit = 500)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT at, user_name, machine, action, target, details FROM audit_log ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<AuditEntry>();
        while (reader.Read())
        {
            result.Add(new AuditEntry(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return result;
    }
}
