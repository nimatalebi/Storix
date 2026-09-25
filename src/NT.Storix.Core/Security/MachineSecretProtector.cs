using System.Security.Cryptography;
using System.Text;

namespace NT.Storix.Core.Security;

/// <summary>
/// Encrypts secrets with Windows DPAPI using the <c>LocalMachine</c> scope so that both the service
/// (LocalSystem) and the manager UI can read them, while the database file alone is useless on another machine.
/// On non-Windows platforms (development only) values are stored as-is.
/// </summary>
public sealed class MachineSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "NT.Storix.Secrets.v1"u8.ToArray();

    public string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText) || plainText.StartsWith(Prefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows())
        {
            return plainText;
        }

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(cipher);
    }

    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText) || !protectedText.StartsWith(Prefix, StringComparison.Ordinal))
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
}
