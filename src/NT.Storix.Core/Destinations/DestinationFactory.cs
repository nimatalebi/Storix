using NT.Storix.Core.Models;

namespace NT.Storix.Core.Destinations;

public interface IDestinationFactory
{
    IBackupDestination Create(DestinationDefinition definition);
}

public sealed class DestinationFactory : IDestinationFactory
{
    public IBackupDestination Create(DestinationDefinition definition) => definition.Kind switch
    {
        DestinationKind.LocalFolder => new LocalFolderDestination(definition.LocalFolder, definition.MaxUploadKBps),
        DestinationKind.Ftp => new FtpDestination(definition.Ftp, definition.MaxUploadKBps),
        DestinationKind.Sftp => new SftpDestination(definition.Sftp, definition.MaxUploadKBps),
        DestinationKind.GoogleDrive => new GoogleDriveDestination(definition.GoogleDrive, definition.MaxUploadKBps),
        DestinationKind.S3 => new S3Destination(definition.S3, definition.MaxUploadKBps),
        DestinationKind.AzureBlob => new AzureBlobDestination(definition.AzureBlob, definition.MaxUploadKBps),
        DestinationKind.WebDav => new WebDavDestination(definition.WebDav, definition.MaxUploadKBps),
        DestinationKind.Dropbox => new DropboxDestination(definition.Dropbox, definition.MaxUploadKBps),
        DestinationKind.OneDrive => new OneDriveDestination(definition.OneDrive, definition.Id, definition.MaxUploadKBps),
        DestinationKind.Rclone => new RcloneDestination(definition.Rclone, definition.MaxUploadKBps),
        _ => throw new NotSupportedException($"Destination kind {definition.Kind} is not supported."),
    };
}
