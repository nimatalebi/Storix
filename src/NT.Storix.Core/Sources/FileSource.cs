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

        foreach (var rawPath in options.Paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawPath.Trim()));

            if (File.Exists(path))
            {
                entries.Add(new ArchiveEntry(path, UniqueRoot(Path.GetFileName(path), usedRoots), options.SkipLockedFiles));
            }
            else if (Directory.Exists(path))
            {
                var root = UniqueRoot(new DirectoryInfo(path).Name is { Length: > 0 } name ? name.TrimEnd(':') : "root", usedRoots);
                foreach (var file in Enumerate(path, context))
                {
                    var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                    entries.Add(new ArchiveEntry(file, $"{root}/{relative}", options.SkipLockedFiles));
                }
            }
            else
            {
                context.Log.Warn($"Path not found, skipped: {path}");
            }
        }

        context.Log.Info($"Collected {entries.Count} file(s).");
        return Task.FromResult(new SourceSnapshot(entries));
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
