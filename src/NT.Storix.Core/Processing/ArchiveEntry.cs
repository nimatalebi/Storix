namespace NT.Storix.Core.Processing;

/// <summary>A file on disk that will be written into the backup archive.</summary>
/// <param name="SourcePath">Full path of the file to read.</param>
/// <param name="EntryName">Relative path inside the archive (forward slashes).</param>
/// <param name="Optional">When true, the file is skipped (with a warning) if it cannot be read.</param>
public sealed record ArchiveEntry(string SourcePath, string EntryName, bool Optional = false)
{
    /// <summary>Temporary file produced by the source (e.g. a database dump) that must be deleted after the run.</summary>
    public bool DeleteAfterRun { get; init; }
}
