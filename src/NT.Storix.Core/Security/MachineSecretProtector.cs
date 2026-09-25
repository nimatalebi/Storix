using System.Security.Cryptography;
using System.Text;

namespace NT.Storix.Core.Security;

/// <summary>
/// Protects secrets stored in the database. On Windows it uses DPAPI with the <c>LocalMachine</c> scope so that
/// both the service (LocalSystem) and the manager UI can read them, while the database file alone is useless on
/// another machine. On Linux it uses AES-256-GCM with a random machine key in <c>secret.key</c> next to the
/// database, readable by root only (the database alone is useless without it).
/// </summary>
public sealed class MachineSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";
    private const string KeyFilePrefix = "aesgcm:";
    private static readonly byte[] Entropy = "NT.Storix.Secrets.v1"u8.ToArray();
    private readonly string _keyPath;

    public MachineSecretProtector()
        : this(Path.Combine(StorixPaths.DataDirectory, "secret.key"))
    {
    }

    /// <param name="keyPath">Key file used on non-Windows platforms.</param>
    public MachineSecretProtector(string keyPath) => _keyPath = keyPath;

    public string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText) || plainText.StartsWith(Prefix, StringComparison.Ordinal) || plainText.StartsWith(KeyFilePrefix, StringComparison.Ordinal))
        {
            return plainText;
        }

        if (!OperatingSystem.IsWindows())
        {
            return KeyFilePrefix + Convert.ToBase64String(SealWithKeyFile(Encoding.UTF8.GetBytes(plainText)));
        }

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(cipher);
    }

    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return protectedText;
        }

        if (protectedText.StartsWith(KeyFilePrefix, StringComparison.Ordinal))
        {
            return Encoding.UTF8.GetString(OpenWithKeyFile(Convert.FromBase64String(protectedText[KeyFilePrefix.Length..])));
        }

        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return protectedText;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI protected secrets can only be read on Windows.");
        }

        var plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedText[Prefix.Length..]), Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(plain);
    }

    // Layout: nonce (12) | tag (16) | ciphertext.
    private byte[] SealWithKeyFile(byte[] plain)
    {
        var result = new byte[12 + 16 + plain.Length];
        var nonce = result.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(LoadOrCreateKey(create: true), 16);
        aes.Encrypt(nonce, plain, result.AsSpan(28), result.AsSpan(12, 16), Entropy);
        return result;
    }

    private byte[] OpenWithKeyFile(byte[] sealedData)
    {
        if (sealedData.Length < 28)
        {
            throw new CryptographicException("Invalid protected secret.");
        }

        var plain = new byte[sealedData.Length - 28];
        using var aes = new AesGcm(LoadOrCreateKey(create: false), 16);
        aes.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(28), sealedData.AsSpan(12, 16), plain, Entropy);
        return plain;
    }

    private byte[] LoadOrCreateKey(bool create)
    {
        if (File.Exists(_keyPath))
        {
            var key = File.ReadAllBytes(_keyPath);
            return key.Length == 32 ? key : throw new CryptographicException($"The key file '{_keyPath}' is invalid.");
        }

        if (!create)
        {
            throw new CryptographicException($"The key file '{_keyPath}' is missing: secrets in this database cannot be read.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_keyPath))!);
        var fresh = RandomNumberGenerator.GetBytes(32);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using var stream = new FileStream(_keyPath, options);
            stream.Write(fresh);
            return fresh;
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            return LoadOrCreateKey(create: false); // Created concurrently by the service or the CLI.
        }
    }
}
