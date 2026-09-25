namespace NT.Storix.Core.Security;

/// <summary>Protects secrets stored in the local metadata database.</summary>
public interface ISecretProtector
{
    string? Protect(string? plainText);

    string? Unprotect(string? protectedText);
}
