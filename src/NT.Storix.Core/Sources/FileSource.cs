using System.IO.Enumeration;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Sources;

/// <summary>Collects files and folders. Files are streamed directly into the archive (no copy).</summary>
public sealed class FileSource(FileSourceOptions options) : IBackupSource
{
    public Task<SourceSnapshot> PrepareAsync(SourceContext context, CancellationToken cancellationToken)
    {
        if (options.Paths.Count == 0)
        {
            throw new InvalidOperationException("No files or folders selected.");
        }

        var entries = new List<ArchiveEntry>();
        var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = options.Paths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim())))
            .ToList();
        var snapshots = options.UseVss ? CreateSnapshots(paths, context) : [];

        try
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Read from the shadow copy when one exists for this volume.
                var snapshot = snapshots.FirstOrDefault(s => path.StartsWith(s.Volume, StringComparison.OrdinalIgnoreCase));
                var readPath = snapshot?.MapPath(path) ?? path;

                if (File.Exists(readPath))
                {
                    entries.Add(new ArchiveEntry(readPath, UniqueRoot(Path.GetFileName(path), usedRoots), options.SkipLockedFiles));
                }
                else if (Directory.Exists(readPath))
                {
                    var root = UniqueRoot(new DirectoryInfo(path).Name is { Length: > 0 } name ? name.TrimEnd(':') : "root", usedRoots);
                    foreach (var file in Enumerate(readPath, context))
                    {
                        var relative = Path.GetRelativePath(readPath, file).Replace('\\', '/');
                        entries.Add(new ArchiveEntry(file, $"{root}/{relative}", options.SkipLockedFiles));
                    }
                }
                else
                {
                    context.Log.Warn($"Path not found, skipped: {path}");
                }
            }
        }
        catch
        {
            foreach (var snapshot in snapshots)
            {
                snapshot.Dispose();
            }

            throw;
        }

        context.Log.Info($"Collected {entries.Count} file(s){(snapshots.Count > 0 ? " from shadow copies" : string.Empty)}.");
        return Task.FromResult(new SourceSnapshot(entries) { Resources = snapshots });
    }

    private static List<IVolumeSnapshot> CreateSnapshots(IEnumerable<string> paths, SourceContext context)
    {
        var snapshots = new List<IVolumeSnapshot>();
        if (!OperatingSystem.IsWindows())
        {
            context.Log.Warn("Volume Shadow Copy is only available on Windows; reading live files.");
            return snapshots;
        }

        foreach (var volume in paths.Select(Path.GetPathRoot).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (volume!.StartsWith(@"\\", StringComparison.Ordinal))
            {
                context.Log.Warn($"Volume Shadow Copy is not available for network paths ({volume}); reading live files.");
                continue;
            }

            try
            {
                snapshots.Add(VolumeShadowCopy.Create(volume));
                context.Log.Info($"Created a shadow copy of {volume}.");
            }
            catch (Exception ex)
            {
                context.Log.Warn($"Could not create a shadow copy of {volume} ({ex.Message}); reading live files.");
            }
        }

        return snapshots;
    }

    private IEnumerable<string> Enumerate(string root, SourceContext context)
    {
        var enumeration = new FileSystemEnumerable<string>(
            root,
            (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = options.IncludeSubdirectories,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && !IsExcluded(entry.FileName),
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsExcluded(entry.FileName),
        };

        foreach (var file in enumeration)
        {
            yield return file;
        }
    }

    private bool IsExcluded(ReadOnlySpan<char> name)
    {
        foreach (var pattern in options.ExcludePatterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern) && FileSystemName.MatchesSimpleExpression(pattern.Trim(), name, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    private static string UniqueRoot(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = $"{name}_{i}";
        }

        return candidate;
    }
}
