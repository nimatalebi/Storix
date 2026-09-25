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
        DestinationKind.LocalFolder => new LocalFolderDestination(definition.LocalFolder),
        DestinationKind.Ftp => new FtpDestination(definition.Ftp),
        DestinationKind.Sftp => new SftpDestination(definition.Sftp),
        DestinationKind.GoogleDrive => new GoogleDriveDestination(definition.GoogleDrive),
        _ => throw new NotSupportedException($"Destination kind {definition.Kind} is not supported."),
    };
}
