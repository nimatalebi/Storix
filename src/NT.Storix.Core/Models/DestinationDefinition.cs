using System.ComponentModel;

namespace NT.Storix.Core.Models;

public enum DestinationKind
{
    LocalFolder,
    Ftp,
    Sftp,
    GoogleDrive,
    S3,
    AzureBlob,
    WebDav,
    Dropbox,
    OneDrive,
    /// <summary>Any of the 40+ providers supported by rclone.</summary>
    Rclone,
}

public sealed class DestinationDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Destination";

    public bool Enabled { get; set; } = true;

    public DestinationKind Kind { get; set; } = DestinationKind.LocalFolder;

    /// <summary>Upload bandwidth limit in KB/s. 0 means unlimited.</summary>
    public int MaxUploadKBps { get; set; }

    public LocalFolderOptions LocalFolder { get; set; } = new();

    public FtpOptions Ftp { get; set; } = new();

    public SftpOptions Sftp { get; set; } = new();

    public GoogleDriveOptions GoogleDrive { get; set; } = new();

    public S3Options S3 { get; set; } = new();

    public AzureBlobOptions AzureBlob { get; set; } = new();

    public WebDavOptions WebDav { get; set; } = new();

    public DropboxOptions Dropbox { get; set; } = new();

    public OneDriveOptions OneDrive { get; set; } = new();

    public RcloneOptions Rclone { get; set; } = new();

    public object ActiveOptions => Kind switch
    {
        DestinationKind.LocalFolder => LocalFolder,
        DestinationKind.Ftp => Ftp,
        DestinationKind.Sftp => Sftp,
        DestinationKind.GoogleDrive => GoogleDrive,
        DestinationKind.S3 => S3,
        DestinationKind.AzureBlob => AzureBlob,
        DestinationKind.WebDav => WebDav,
        DestinationKind.Dropbox => Dropbox,
        DestinationKind.OneDrive => OneDrive,
        DestinationKind.Rclone => Rclone,
        _ => throw new NotSupportedException($"Destination kind {Kind} is not supported."),
    };

    public override string ToString() => $"{Name} ({Kind})";
}

public sealed class LocalFolderOptions
{
    [Category("Target"), Description("Local folder or UNC path (\\\\server\\share\\backups).")]
    public string Path { get; set; } = string.Empty;

    [Category("Network share"), Description("Optional user for a UNC path, e.g. NAS\\backup or DOMAIN\\user. Empty = the service account.")]
    public string? UserName { get; set; }

    [Category("Network share"), PasswordPropertyText(true), Secret]
    public string? Password { get; set; }
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

public enum GoogleDriveAuthMode
{
    /// <summary>Service account JSON key (use a folder in a Shared Drive).</summary>
    ServiceAccount,
    /// <summary>Your own Google account (OAuth), e.g. personal "My Drive".</summary>
    UserAccount,
}

public sealed class GoogleDriveOptions
{
    [Category("Credentials"), Description("ServiceAccount: key file + Shared Drive. UserAccount: sign in with your Google account (My Drive).")]
    public GoogleDriveAuthMode AuthMode { get; set; } = GoogleDriveAuthMode.ServiceAccount;

    [Category("Credentials"), Description("ServiceAccount: path to a Google Cloud service account JSON key file.")]
    public string ServiceAccountKeyPath { get; set; } = string.Empty;

    [Category("Credentials"), Description("UserAccount: OAuth client id (Google Cloud console → Credentials → OAuth client ID → Desktop app).")]
    public string? OAuthClientId { get; set; }

    [Category("Credentials"), Description("UserAccount: OAuth client secret."), PasswordPropertyText(true), Secret]
    public string? OAuthClientSecret { get; set; }

    [Category("Credentials"), Description("UserAccount: filled in by 'Sign in with Google'."), PasswordPropertyText(true), Secret, ReadOnly(true)]
    public string? RefreshToken { get; set; }

    [Category("Credentials"), Description("UserAccount: the signed-in Google account."), ReadOnly(true)]
    public string? SignedInAs { get; set; }

    [Category("Target"), Description("Id of the Drive folder (from its URL). UserAccount: empty = root of My Drive.")]
    public string FolderId { get; set; } = string.Empty;

    [Category("Transfer"), Description("Upload chunk size in MB (multiple of 0.25).")]
    public int ChunkSizeMb { get; set; } = 16;
}

public sealed class S3Options
{
    [Category("Connection"), Description("Endpoint URL for S3-compatible storage (MinIO, Wasabi, Cloudflare R2, Backblaze B2...). Leave empty for Amazon S3.")]
    public string? ServiceUrl { get; set; }

    [Category("Connection"), Description("Region, e.g. eu-central-1. For S3-compatible providers use the value they document (often us-east-1 or auto).")]
    public string Region { get; set; } = "us-east-1";

    [Category("Connection"), Description("Use path-style URLs (required by MinIO and most self-hosted servers).")]
    public bool ForcePathStyle { get; set; }

    [Category("Credentials")]
    public string AccessKeyId { get; set; } = string.Empty;

    [Category("Credentials"), PasswordPropertyText(true), Secret]
    public string? SecretAccessKey { get; set; }

    [Category("Target")]
    public string BucketName { get; set; } = string.Empty;

    [Category("Target"), Description("Optional folder (key prefix) inside the bucket, e.g. backups/server1")]
    public string? Prefix { get; set; }

    [Category("Transfer"), Description("Storage class, e.g. STANDARD, STANDARD_IA, GLACIER_IR. Empty = bucket default.")]
    public string? StorageClass { get; set; }

    [Category("Transfer"), Description("Multipart part size in MB (5-512).")]
    public int PartSizeMb { get; set; } = 16;

    [Category("Immutability"), Description("S3 Object Lock for new backups (the bucket must have Object Lock enabled). Compliance cannot be shortened by anyone, Governance only by special permission.")]
    public S3ObjectLockMode ObjectLockMode { get; set; } = S3ObjectLockMode.None;

    [Category("Immutability"), Description("Days each backup stays locked (cannot be deleted or overwritten).")]
    public int ObjectLockDays { get; set; } = 30;
}

public enum S3ObjectLockMode
{
    None,
    Governance,
    Compliance,
}

public sealed class AzureBlobOptions
{
    [Category("Connection"), Description("Storage account connection string (Access keys page of the storage account)."), PasswordPropertyText(true), Secret]
    public string? ConnectionString { get; set; }

    [Category("Target")]
    public string Container { get; set; } = "backups";

    [Category("Target"), Description("Optional folder (prefix) inside the container.")]
    public string? Prefix { get; set; }

    [Category("Transfer"), Description("Access tier: Hot, Cool, Cold or Archive. Empty = account default.")]
    public string? AccessTier { get; set; }
}

public sealed class WebDavOptions
{
    [Category("Connection"), Description("Folder URL, e.g. https://cloud.example.com/remote.php/dav/files/USER/backups (Nextcloud) or https://nas:5006/backups")]
    public string Url { get; set; } = string.Empty;

    [Category("Credentials")]
    public string UserName { get; set; } = string.Empty;

    [Category("Credentials"), Description("Password or app password."), PasswordPropertyText(true), Secret]
    public string? Password { get; set; }

    [Category("Connection"), Description("Accept any server certificate (self-signed NAS).")]
    public bool AcceptAnyCertificate { get; set; }
}

public sealed class DropboxOptions
{
    [Category("Credentials"), Description("App key of your Dropbox app (App Console). Redirect URI: http://localhost:53682/")]
    public string AppKey { get; set; } = string.Empty;

    [Category("Credentials"), Description("Filled in by 'Sign in'."), PasswordPropertyText(true), Secret, ReadOnly(true)]
    public string? RefreshToken { get; set; }

    [Category("Credentials"), ReadOnly(true)]
    public string? SignedInAs { get; set; }

    [Category("Target"), Description("Folder, e.g. /Backups/server1")]
    public string Folder { get; set; } = "/Storix";
}

public sealed class OneDriveOptions
{
    [Category("Credentials"), Description("Application (client) id of an Azure app registration (public client, redirect http://localhost:53682/).")]
    public string ClientId { get; set; } = string.Empty;

    [Category("Credentials"), Description("consumers (personal OneDrive), organizations, common, or your tenant id (OneDrive for Business / SharePoint).")]
    public string Tenant { get; set; } = "common";

    [Category("Credentials"), Description("Filled in by 'Sign in'."), PasswordPropertyText(true), Secret, ReadOnly(true)]
    public string? RefreshToken { get; set; }

    [Category("Credentials"), ReadOnly(true)]
    public string? SignedInAs { get; set; }

    [Category("Target"), Description("Folder in your OneDrive, e.g. Backups/server1")]
    public string Folder { get; set; } = "Storix";
}

public sealed class RcloneOptions
{
    [Category("Tool"), Description("Full path to rclone(.exe). Empty = on PATH.")]
    public string? RclonePath { get; set; }

    [Category("Tool"), Description("Optional rclone.conf path (created with 'rclone config'). Empty = rclone default.")]
    public string? ConfigPath { get; set; }

    [Category("Target"), Description("Remote and folder, e.g. b2:bucket/backups or mydrive:Backups/server1")]
    public string Remote { get; set; } = string.Empty;

    [Category("Tool"), Description("Extra rclone flags, e.g. --b2-hard-delete")]
    public string? ExtraArguments { get; set; }
}
