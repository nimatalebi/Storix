namespace NT.Storix.Core.Models;

public enum ArchiveCompression
{
    None,
    Fastest,
    Optimal,
    Smallest,
}

public sealed class ProcessingOptions
{
    public ArchiveCompression Compression { get; set; } = ArchiveCompression.Optimal;

    public bool Encrypt { get; set; }

    [Secret]
    public string? EncryptionPassword { get; set; }

    /// <summary>Re-reads the archive (and decrypts it) before uploading to make sure it is valid.</summary>
    public bool VerifyArchive { get; set; } = true;
}
