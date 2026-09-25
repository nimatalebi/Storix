using System.Security.Cryptography;

namespace NT.Storix.Core.Security;

/// <summary>
/// Public-key encryption support. Key material travels through the same "secret" string the rest of the
/// pipeline uses for passwords, with a marker that distinguishes it from a password.
/// </summary>
public static class PrivateKeySecret
{
    private const string PublicMarker = "\u0000storix-publickey:";
    private const string PrivateMarker = "\u0000storix-privatekey:";
    private const string PassphraseMarker = "\u0000passphrase:";

    /// <summary>Secret string that makes the encryptor use the public key (encryption only).</summary>
    public static string ForPublicKey(string publicKeyPem) => PublicMarker + publicKeyPem.Trim();

    /// <summary>Secret string that lets the decryptor use a private key (optionally passphrase-protected).</summary>
    public static string ForPrivateKey(string privateKeyPem, string? passphrase) =>
        PrivateMarker + privateKeyPem.Trim() + (string.IsNullOrEmpty(passphrase) ? string.Empty : PassphraseMarker + passphrase);

    public static bool IsPublicKey(string secret) => secret.StartsWith(PublicMarker, StringComparison.Ordinal);

    public static string PublicKeyPem(string secret) => secret[PublicMarker.Length..];

    /// <summary>Loads the private key from a secret created with <see cref="ForPrivateKey"/>, or null for passwords.</summary>
    public static RSA? LoadPrivateKey(string secret)
    {
        if (!secret.StartsWith(PrivateMarker, StringComparison.Ordinal))
        {
            return null;
        }

        var body = secret[PrivateMarker.Length..];
        var split = body.IndexOf(PassphraseMarker, StringComparison.Ordinal);
        var pem = split < 0 ? body : body[..split];
        var passphrase = split < 0 ? null : body[(split + PassphraseMarker.Length)..];

        var rsa = RSA.Create();
        try
        {
            if (pem.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(passphrase))
                {
                    throw new CryptographicException("The private key is protected: enter its passphrase.");
                }

                rsa.ImportFromEncryptedPem(pem, passphrase);
            }
            else
            {
                rsa.ImportFromPem(pem);
            }

            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>Creates an RSA-4096 key pair. The private key is encrypted (AES-256) when a passphrase is given.</summary>
    public static (string PublicKeyPem, string PrivateKeyPem) GenerateKeyPair(string? passphrase)
    {
        using var rsa = RSA.Create(4096);
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        var privatePem = string.IsNullOrEmpty(passphrase)
            ? rsa.ExportPkcs8PrivateKeyPem()
            : rsa.ExportEncryptedPkcs8PrivateKeyPem(passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
        return (publicPem, privatePem);
    }

    /// <summary>Short fingerprint of a public key for display (first 16 hex digits of its SHA-256).</summary>
    public static string Fingerprint(string publicKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return Convert.ToHexStringLower(Processing.AesFileEncryptor.KeyId(rsa))[..16];
    }
}
