using System.Net;
using System.Security.Cryptography;
using NT.Storix.Core.Updates;

namespace NT.Storix.Core.Tests;

public class UpdateTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private const string ReleasesJson = """
        [
          { "tag_name": "v1.3.0-beta.1", "prerelease": true, "draft": false, "html_url": "https://x/1.3.0-beta.1",
            "assets": [ { "name": "Storix-1.3.0-x64.msi", "browser_download_url": "https://x/b.msi" }, { "name": "SHA256SUMS.txt", "browser_download_url": "https://x/b.txt" } ] },
          { "tag_name": "v1.4.0", "prerelease": false, "draft": true, "assets": [] },
          { "tag_name": "v1.2.1", "prerelease": false, "draft": false, "html_url": "https://x/1.2.1", "body": "Fixes",
            "assets": [ { "name": "Storix-1.2.1-x64.msi", "browser_download_url": "https://x/a.msi" }, { "name": "SHA256SUMS.txt", "browser_download_url": "https://x/a.txt" } ] },
          { "tag_name": "v1.2.2", "prerelease": false, "draft": false, "assets": [] }
        ]
        """;

    [Theory]
    [InlineData("1.2.0", "1.1.9", 1)]
    [InlineData("1.2.0", "1.2.0-beta.2", 1)]
    [InlineData("1.2.0-beta.10", "1.2.0-beta.2", 1)]
    [InlineData("1.2.0-alpha", "1.2.0-beta", -1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("0.1.0-preview", "0.1.0-preview", 0)]
    public void Versions_compare_semantically(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(UpdateChecker.Compare(a, b)));
    }

    [Fact]
    public void Picks_the_newest_stable_release_with_an_installer()
    {
        var releases = UpdateChecker.ParseReleases(ReleasesJson);

        Assert.Equal(3, releases.Count); // The draft is ignored.
        Assert.Equal("1.2.1", UpdateChecker.SelectUpdate(releases, "1.2.0", includePrerelease: false)!.Version);
        Assert.Equal("1.3.0-beta.1", UpdateChecker.SelectUpdate(releases, "1.2.0", includePrerelease: true)!.Version);
        Assert.Null(UpdateChecker.SelectUpdate(releases, "1.2.1", includePrerelease: false));
    }

    [Fact]
    public async Task Download_is_checked_against_the_published_checksums()
    {
        using var temp = new TempDirectory();
        var installer = RandomNumberGenerator.GetBytes(4096);
        var hash = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant();
        var sums = $"{hash} *Storix-1.2.1-x64.msi\n{new string('0', 64)} *storix-1.2.1-x64.exe\n";
        var tamper = false;
        using var http = new HttpClient(new FakeHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath.EndsWith(".txt")
                ? new StringContent(sums)
                : new ByteArrayContent(tamper ? [.. installer, 1] : installer),
        }));
        var release = UpdateChecker.SelectUpdate(UpdateChecker.ParseReleases(ReleasesJson), "1.0.0", false)!;

        var path = await UpdateChecker.DownloadAsync(http, release, temp.Combine("dl"), null, CancellationToken.None);
        Assert.Equal(installer, await File.ReadAllBytesAsync(path));

        tamper = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadAsync(http, release, temp.Combine("dl2"), null, CancellationToken.None));
        Assert.False(File.Exists(temp.Combine("dl2", "Storix-1.2.1-x64.msi")));
    }
}
