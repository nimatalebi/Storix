using System.Security.Cryptography;

namespace NT.Storix.Core.Processing;

public static class Checksum
{
    public const string SidecarExtension = ".sha256";

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Writes a <c>sha256sum</c> compatible sidecar file next to <paramref name="path"/>.</summary>
    public static async Task<string> WriteSidecarAsync(string path, string hash, CancellationToken cancellationToken)
    {
        var sidecar = path + SidecarExtension;
        await File.WriteAllTextAsync(sidecar, $"{hash} *{Path.GetFileName(path)}\n", cancellationToken);
        return sidecar;
    }
}
