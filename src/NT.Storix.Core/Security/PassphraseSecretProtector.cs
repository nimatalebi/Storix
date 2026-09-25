using System.Security.Cryptography;
using System.Text;

namespace NT.Storix.Core.Security;

/// <summary>
/// Protects individual secret values with AES-256-GCM using a key derived from a passphrase.
/// Used to carry secrets inside exported configuration files.
/// </summary>
public sealed class PassphraseSecretProtector : ISecretProtector
{
    private const string Prefix = "pass:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public PassphraseSecretProtector(string passphrase, byte[] salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        _key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
    }

    public string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return plainText;
        }

        var plain = Encoding.UTF8.GetBytes(plainText);
        var buffer = new byte[NonceSize + plain.Length + TagSize];
        var nonce = buffer.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, buffer.AsSpan(NonceSize, plain.Length), buffer.AsSpan(NonceSize + plain.Length, TagSize));
        return Prefix + Convert.ToBase64String(buffer);
    }

    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText) || !protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return protectedText;
        }

        var buffer = Convert.FromBase64String(protectedText[Prefix.Length..]);
        var cipherLength = buffer.Length - NonceSize - TagSize;
        if (cipherLength < 0)
        {
            throw new CryptographicException("Invalid protected value.");
        }

        var plain = new byte[cipherLength];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(
            buffer.AsSpan(0, NonceSize),
            buffer.AsSpan(NonceSize, cipherLength),
            buffer.AsSpan(NonceSize + cipherLength, TagSize),
            plain);
        return Encoding.UTF8.GetString(plain);
    }
}
