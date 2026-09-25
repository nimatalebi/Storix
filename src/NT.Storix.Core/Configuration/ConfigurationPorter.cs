using System.Security.Cryptography;
using System.Text.Json;
using NT.Storix.Core.Models;
using NT.Storix.Core.Security;

namespace NT.Storix.Core.Configuration;

/// <summary>Portable configuration file (jobs and optionally global settings).</summary>
public sealed class ConfigurationPackage
{
    public const string FormatName = "storix-config";
    public const int CurrentVersion = 1;

    /// <summary>Always <see cref="FormatName"/> in a valid file (no default, so foreign JSON is detected).</summary>
    public string? Format { get; set; }

    public int Version { get; set; }

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? MachineName { get; set; }

    /// <summary>Present when secrets are included and protected with a passphrase.</summary>
    public PassphraseInfo? Secrets { get; set; }

    public AppSettings? Settings { get; set; }

    public List<BackupJob> Jobs { get; set; } = [];
}

public sealed class PassphraseInfo
{
    public string Salt { get; set; } = string.Empty;

    public int Iterations { get; set; }

    /// <summary>Encrypted known value used to check the passphrase before importing.</summary>
    public string Check { get; set; } = string.Empty;
}

/// <summary>Imports and exports backup configurations as JSON.</summary>
public static class ConfigurationPorter
{
    private const int Iterations = 600_000;
    private const string CheckValue = "storix";

    /// <summary>
    /// Serializes the configuration. Without a passphrase every secret (passwords, connection strings,
    /// encryption keys) is removed. With a passphrase secrets are kept, encrypted with AES-256-GCM.
    /// </summary>
    public static string Export(IEnumerable<BackupJob> jobs, AppSettings? settings, string? passphrase)
    {
        var package = new ConfigurationPackage
        {
            Format = ConfigurationPackage.FormatName,
            Version = ConfigurationPackage.CurrentVersion,
            MachineName = Environment.MachineName,
            Settings = settings is null ? null : StorixJson.Clone(settings),
            Jobs = jobs.Select(StorixJson.Clone).ToList(),
        };

        if (string.IsNullOrEmpty(passphrase))
        {
            SecretWalker.Transform(package, _ => null);
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var protector = new PassphraseSecretProtector(passphrase, salt, Iterations);
            SecretWalker.Transform(package, protector.Protect);
            package.Secrets = new PassphraseInfo
            {
                Salt = Convert.ToBase64String(salt),
                Iterations = Iterations,
                Check = protector.Protect(CheckValue)!,
            };
        }

        return JsonSerializer.Serialize(package, StorixJson.Indented);
    }

    public static bool RequiresPassphrase(string json) => Parse(json).Secrets is not null;

    /// <summary>Parses an exported file and decrypts its secrets.</summary>
    /// <exception cref="InvalidDataException">The file is not a valid configuration.</exception>
    /// <exception cref="UnauthorizedAccessException">The passphrase is missing or wrong.</exception>
    public static ConfigurationPackage Import(string json, string? passphrase)
    {
        var package = Parse(json);

        if (package.Secrets is { } info)
        {
            if (string.IsNullOrEmpty(passphrase))
            {
                throw new UnauthorizedAccessException("This configuration contains protected secrets. A passphrase is required.");
            }

            var protector = new PassphraseSecretProtector(passphrase, Convert.FromBase64String(info.Salt), info.Iterations);
            try
            {
                if (protector.Unprotect(info.Check) != CheckValue)
                {
                    throw new CryptographicException();
                }
            }
            catch (CryptographicException)
            {
                throw new UnauthorizedAccessException("Wrong passphrase.");
            }

            SecretWalker.Transform(package, protector.Unprotect);
            package.Secrets = null;
        }

        return package;
    }

    private static ConfigurationPackage Parse(string json)
    {
        ConfigurationPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<ConfigurationPackage>(json, StorixJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file is not a valid Storix configuration.", ex);
        }

        if (package is null || package.Format != ConfigurationPackage.FormatName)
        {
            throw new InvalidDataException("The file is not a Storix configuration export.");
        }

        if (package.Version > ConfigurationPackage.CurrentVersion)
        {
            throw new InvalidDataException($"The configuration was exported by a newer version of Storix (format v{package.Version}).");
        }

        return package;
    }
}
