using NT.Storix.Core.Engine;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <param name="StagingDirectory">Empty folder owned by the current run for temporary files (dumps).</param>
public sealed record SourceContext(string StagingDirectory, RunLog Log);

/// <summary>Everything that must go into the archive for one run.</summary>
public sealed record SourceSnapshot(IReadOnlyList<ArchiveEntry> Entries)
{
    /// <summary>SQL Server backup metadata (LSNs) recorded for restore chains.</summary>
    public IReadOnlyList<SqlBackupInfo> SqlBackups { get; init; } = [];

    /// <summary>Resources that must stay alive until the archive is written (e.g. VSS snapshots).</summary>
    public IReadOnlyList<IDisposable> Resources { get; init; } = [];
}

public interface IBackupSource
{
    Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken);
}
