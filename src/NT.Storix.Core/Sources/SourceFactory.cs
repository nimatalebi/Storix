using NT.Storix.Core.Models;

namespace NT.Storix.Core.Sources;

public interface ISourceFactory
{
    IBackupSource Create(SourceDefinition definition);
}

public sealed class SourceFactory : ISourceFactory
{
    public IBackupSource Create(SourceDefinition definition) => definition.Kind switch
    {
        SourceKind.Files => new FileSource(definition.Files),
        SourceKind.SqlServer => new SqlServerSource(definition.SqlServer),
        SourceKind.MongoDb => new MongoDbSource(definition.MongoDb),
        SourceKind.PostgreSql => new PostgreSqlSource(definition.PostgreSql),
        SourceKind.MySql => new MySqlSource(definition.MySql),
        SourceKind.Redis => new RedisSource(definition.Redis),
        SourceKind.Sqlite => new SqliteSource(definition.Sqlite),
        SourceKind.WindowsSystem => new WindowsSystemSource(definition.WindowsSystem),
        SourceKind.DockerVolumes => new DockerVolumesSource(definition.DockerVolumes),
        SourceKind.HyperV => new HyperVSource(definition.HyperV),
        SourceKind.Plugin => Plugins.PluginRegistry.CreateSource(definition.Plugin),
        SourceKind.CopyOf => throw new NotSupportedException("Copy jobs do not produce files; they copy existing backups."),
        _ => throw new NotSupportedException($"Source kind {definition.Kind} is not supported."),
    };
}
