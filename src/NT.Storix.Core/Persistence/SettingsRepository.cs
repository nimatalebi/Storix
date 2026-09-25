using System.Text.Json;
using NT.Storix.Core.Models;
using NT.Storix.Core.Security;

namespace NT.Storix.Core.Persistence;

public sealed class SettingsRepository(StorixDatabase database, ISecretProtector protector)
{
    private const string AppSettingsKey = "app";

    public AppSettings Get()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", AppSettingsKey);

        if (command.ExecuteScalar() is not string json)
        {
            return new AppSettings();
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(json, StorixJson.Options) ?? new AppSettings();
        SecretWalker.Transform(settings, protector.Unprotect);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        var copy = StorixJson.Clone(settings);
        SecretWalker.Transform(copy, protector.Protect);

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", AppSettingsKey);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(copy, StorixJson.Options));
        command.ExecuteNonQuery();
    }

    /// <summary>Reads an internal value (not part of <see cref="AppSettings"/>).</summary>
    public string? GetValue(string key)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", "internal:" + key);
        return command.ExecuteScalar() as string;
    }

    public void SetValue(string key, string value)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", "internal:" + key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
