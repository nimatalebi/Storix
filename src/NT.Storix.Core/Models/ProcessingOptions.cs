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

    /// <summary>
    /// Optional key file. Its SHA-256 is combined with the password, so both are needed to restore
    /// (or only the key file when no password is set).
    /// </summary>
    public string? EncryptionKeyFile { get; set; }

    /// <summary>The user confirmed that the password / key file is stored somewhere safe (recovery sheet).</summary>
    public bool RecoveryInfoConfirmed { get; set; }

    /// <summary>Re-reads the archive (and decrypts it) before uploading to make sure it is valid.</summary>
    public bool VerifyArchive { get; set; } = true;
}
