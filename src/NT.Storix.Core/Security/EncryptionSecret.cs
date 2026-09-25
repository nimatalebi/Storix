using System.Security.Cryptography;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Security;

/// <summary>
/// Builds the secret used by <see cref="Processing.AesFileEncryptor"/> from a password and/or a key file.
/// A password alone is used unchanged, so existing backups stay compatible.
/// </summary>
public static class EncryptionSecret
{
    private const string KeyFileMarker = "\u0000storix-keyfile:";

    public static string? Resolve(ProcessingOptions options) => Combine(options.EncryptionPassword, options.EncryptionKeyFile);

    /// <exception cref="FileNotFoundException">The key file does not exist.</exception>
    public static string? Combine(string? password, string? keyFilePath)
    {
        if (string.IsNullOrWhiteSpace(keyFilePath))
        {
            return string.IsNullOrEmpty(password) ? null : password;
        }

        if (!File.Exists(keyFilePath))
        {
            throw new FileNotFoundException("The encryption key file was not found.", keyFilePath);
        }

        using var stream = File.OpenRead(keyFilePath);
        return (password ?? string.Empty) + KeyFileMarker + Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Creates a new random key file (256 bits, base64 text so it can also be printed).</summary>
    public static void CreateKeyFile(string path)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, key + Environment.NewLine);
    }
}
