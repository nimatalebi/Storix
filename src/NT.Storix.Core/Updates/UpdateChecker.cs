using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Updates;

public sealed record ReleaseInfo(string Version, string Tag, string PageUrl, string? Notes, bool Prerelease, string? InstallerName, string? InstallerUrl, string? ChecksumsUrl);

/// <summary>
/// Looks for newer Storix releases on GitHub and downloads the installer. The download is checked against the
/// release's SHA256SUMS.txt; the caller also checks the installer's code signature before running it.
/// </summary>
public static class UpdateChecker
{
    public const string ReleasesApi = "https://api.github.com/repos/nimatalebi/Storix/releases";
    public const string ChecksumsFile = "SHA256SUMS.txt";

    /// <summary>The newest release that is newer than <paramref name="currentVersion"/>, or null.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(HttpClient http, string currentVersion, bool includePrerelease, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi + "?per_page=20");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(StorixInfo.ProductName, currentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return SelectUpdate(ParseReleases(json), currentVersion, includePrerelease);
    }

    public static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        var releases = new List<ReleaseInfo>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                continue;
            }

            var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
            string? installerName = null, installerUrl = null, checksumsUrl = null;
            if (release.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? string.Empty;
                    var url = asset.GetProperty("browser_download_url").GetString();
                    if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) && name.Contains("x64", StringComparison.OrdinalIgnoreCase))
                    {
                        (installerName, installerUrl) = (name, url);
                    }
                    else if (string.Equals(name, ChecksumsFile, StringComparison.OrdinalIgnoreCase))
                    {
                        checksumsUrl = url;
                    }
                }
            }

            releases.Add(new ReleaseInfo(
                tag.TrimStart('v', 'V'),
                tag,
                release.TryGetProperty("html_url", out var page) ? page.GetString() ?? StorixInfo.RepositoryUrl : StorixInfo.RepositoryUrl,
                release.TryGetProperty("body", out var body) ? body.GetString() : null,
                release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean(),
                installerName,
                installerUrl,
                checksumsUrl));
        }

        return releases;
    }

    public static ReleaseInfo? SelectUpdate(IEnumerable<ReleaseInfo> releases, string currentVersion, bool includePrerelease) =>
        releases
            .Where(r => includePrerelease || !r.Prerelease)
            .Where(r => r.InstallerUrl is not null && r.ChecksumsUrl is not null)
            .Where(r => Compare(r.Version, currentVersion) > 0)
            .OrderByDescending(r => r.Version, Comparer<string>.Create(Compare))
            .FirstOrDefault();

    /// <summary>Compares semantic versions (1.2.0 &gt; 1.2.0-beta.2 &gt; 1.2.0-beta.1 &gt; 1.1.9).</summary>
    public static int Compare(string a, string b)
    {
        static (int[] Numbers, string? Pre) Split(string version)
        {
            var core = version.Split('+')[0];
            var dash = core.IndexOf('-');
            var pre = dash >= 0 ? core[(dash + 1)..] : null;
            var numbers = (dash >= 0 ? core[..dash] : core).Split('.')
                .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
                .ToArray();
            return (numbers, pre);
        }

        var (na, pa) = Split(a);
        var (nb, pb) = Split(b);
        for (var i = 0; i < Math.Max(na.Length, nb.Length); i++)
        {
            var x = i < na.Length ? na[i] : 0;
            var y = i < nb.Length ? nb[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        if (pa is null || pb is null)
        {
            return pa is null ? (pb is null ? 0 : 1) : -1;
        }

        var ia = pa.Split('.');
        var ib = pb.Split('.');
        for (var i = 0; i < Math.Max(ia.Length, ib.Length); i++)
        {
            if (i >= ia.Length || i >= ib.Length)
            {
                return ia.Length.CompareTo(ib.Length);
            }

            var bothNumeric = int.TryParse(ia[i], out var xa) & int.TryParse(ib[i], out var xb);
            var result = bothNumeric ? xa.CompareTo(xb) : string.CompareOrdinal(ia[i], ib[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    /// <summary>Downloads the installer and verifies it against SHA256SUMS.txt. Returns the local path.</summary>
    /// <exception cref="InvalidDataException">The checksum is missing or does not match.</exception>
    public static async Task<string> DownloadAsync(HttpClient http, ReleaseInfo release, string folder, IProgress<string>? status, CancellationToken cancellationToken)
    {
        if (release.InstallerUrl is null || release.ChecksumsUrl is null || release.InstallerName is null)
        {
            throw new InvalidOperationException("This release has no installer.");
        }

        Directory.CreateDirectory(folder);
        status?.Report("Downloading checksums...");
        var sums = await http.GetStringAsync(release.ChecksumsUrl, cancellationToken);
        var expected = ParseChecksums(sums).GetValueOrDefault(release.InstallerName)
                       ?? throw new InvalidDataException($"{ChecksumsFile} has no entry for {release.InstallerName}.");

        var path = Path.Combine(folder, Path.GetFileName(release.InstallerName));
        status?.Report($"Downloading {release.InstallerName}...");
        await using (var input = await http.GetStreamAsync(release.InstallerUrl, cancellationToken))
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        var actual = await Checksum.Sha256Async(path, cancellationToken);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException($"The downloaded installer does not match its published SHA-256 (expected {expected}, got {actual}).");
        }

        return path;
    }

    /// <summary>Parses <c>sha256sum</c> output (<c>hash *name</c> or <c>hash  name</c>).</summary>
    public static IReadOnlyDictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = line.IndexOf(' ');
            if (space == 64)
            {
                result[line[space..].Trim().TrimStart('*')] = line[..space];
            }
        }

        return result;
    }
}
