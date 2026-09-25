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
        _ => throw new NotSupportedException($"Source kind {definition.Kind} is not supported."),
    };
}
