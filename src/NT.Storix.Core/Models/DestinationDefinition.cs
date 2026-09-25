using System.ComponentModel;

namespace NT.Storix.Core.Models;

public enum DestinationKind
{
    LocalFolder,
    Ftp,
    Sftp,
    GoogleDrive,
}

public sealed class DestinationDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Destination";

    public bool Enabled { get; set; } = true;

    public DestinationKind Kind { get; set; } = DestinationKind.LocalFolder;

    public LocalFolderOptions LocalFolder { get; set; } = new();

    public FtpOptions Ftp { get; set; } = new();

    public SftpOptions Sftp { get; set; } = new();

    public GoogleDriveOptions GoogleDrive { get; set; } = new();

    public object ActiveOptions => Kind switch
    {
        DestinationKind.LocalFolder => LocalFolder,
        DestinationKind.Ftp => Ftp,
        DestinationKind.Sftp => Sftp,
        DestinationKind.GoogleDrive => GoogleDrive,
        _ => throw new NotSupportedException($"Destination kind {Kind} is not supported."),
    };

    public override string ToString() => $"{Name} ({Kind})";
}

public sealed class LocalFolderOptions
{
    [Category("Target"), Description("Local folder or UNC path (\\\\server\\share\\backups).")]
    public string Path { get; set; } = string.Empty;
}

public enum FtpEncryption
{
    None,
    Explicit,
    Implicit,
}

public sealed class FtpOptions
{
    [Category("Connection")]
    public string Host { get; set; } = string.Empty;

    [Category("Connection")]
    public int Port { get; set; } = 21;

    [Category("Connection"), Description("FTPS mode.")]
    public FtpEncryption Encryption { get; set; } = FtpEncryption.Explicit;

    [Category("Connection"), Description("Accept any server certificate (self-signed servers).")]
    public bool AcceptAnyCertificate { get; set; }

    [Category("Connection"), Description("Use passive mode.")]
    public bool Passive { get; set; } = true;

    [Category("Credentials")]
    public string UserName { get; set; } = string.Empty;

    [Category("Credentials"), PasswordPropertyText(true), Secret]
    public string? Password { get; set; }

    [Category("Target"), Description("Remote folder, e.g. /backups/server1")]
    public string RemotePath { get; set; } = "/";
}

public sealed class SftpOptions
{
    [Category("Connection")]
    public string Host { get; set; } = string.Empty;

    [Category("Connection")]
    public int Port { get; set; } = 22;

    [Category("Connection"), Description("Optional SHA256 host key fingerprint (base64). When set, the server key must match.")]
    public string? HostKeyFingerprint { get; set; }

    [Category("Credentials")]
    public string UserName { get; set; } = string.Empty;

    [Category("Credentials"), PasswordPropertyText(true), Secret]
    public string? Password { get; set; }

    [Category("Credentials"), Description("Optional path to a private key file (OpenSSH / PuTTY format).")]
    public string? PrivateKeyPath { get; set; }

    [Category("Credentials"), PasswordPropertyText(true), Secret]
    public string? PrivateKeyPassphrase { get; set; }

    [Category("Target"), Description("Remote folder, e.g. /home/backup/server1")]
    public string RemotePath { get; set; } = ".";
}

public sealed class GoogleDriveOptions
{
    [Category("Credentials"), Description("Path to a Google Cloud service account JSON key file.")]
    public string ServiceAccountKeyPath { get; set; } = string.Empty;

    [Category("Target"), Description("Id of the Drive folder (preferably inside a Shared Drive) shared with the service account.")]
    public string FolderId { get; set; } = string.Empty;

    [Category("Transfer"), Description("Upload chunk size in MB (multiple of 0.25).")]
    public int ChunkSizeMb { get; set; } = 16;
}
