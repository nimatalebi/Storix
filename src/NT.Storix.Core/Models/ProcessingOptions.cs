namespace NT.Storix.Core.Models;

public enum ArchiveCompression
{
    None,
    Fastest,
    Optimal,
    Smallest,
    /// <summary>Zstandard (level 3): about as small as Optimal, several times faster. Produces <c>.zip.zst</c>.</summary>
    Zstd,
    /// <summary>Zstandard (level 19): smallest files, slow to create, fast to restore. Produces <c>.zip.zst</c>.</summary>
    ZstdSmallest,
}

public enum EncryptionMode
{
    /// <summary>Password (and optional key file): whoever can run backups can also restore them.</summary>
    Password,
    /// <summary>
    /// Public key: the server only holds the public key; restoring needs the private key kept offline.
    /// A compromised server cannot read or re-encrypt old backups.
    /// </summary>
    PublicKey,
}

public sealed class ProcessingOptions
{
    public ArchiveCompression Compression { get; set; } = ArchiveCompression.Optimal;

    public bool Encrypt { get; set; }

    public EncryptionMode EncryptionMode { get; set; } = EncryptionMode.Password;

    [Secret]
    public string? EncryptionPassword { get; set; }

    /// <summary>Public key (PEM) for <see cref="EncryptionMode.PublicKey"/>. Not a secret.</summary>
    public string? PublicKeyPem { get; set; }

    /// <summary>
    /// Optional key file. Its SHA-256 is combined with the password, so both are needed to restore
    /// (or only the key file when no password is set).
    /// </summary>
    public string? EncryptionKeyFile { get; set; }

    /// <summary>The user confirmed that the password / key file is stored somewhere safe (recovery sheet).</summary>
    public bool RecoveryInfoConfirmed { get; set; }

    /// <summary>Re-reads the archive (and decrypts it) before uploading to make sure it is valid.</summary>
    public bool VerifyArchive { get; set; } = true;

    /// <summary>
    /// Split the backup into volumes of this size (MB) before uploading. 0 disables splitting.
    /// Useful for providers with file-size limits and to resume large uploads chunk by chunk.
    /// </summary>
    public int SplitSizeMb { get; set; }
}
