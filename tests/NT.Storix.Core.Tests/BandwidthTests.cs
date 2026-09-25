using System.Diagnostics;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Scheduling;

namespace NT.Storix.Core.Tests;

public class BandwidthTests
{
    [Fact]
    public async Task Throttled_stream_respects_the_rate()
    {
        var data = new byte[96 * 1024];
        await using var stream = new ThrottledStream(new MemoryStream(data), 64 * 1024);

        var watch = Stopwatch.StartNew();
        await stream.CopyToAsync(Stream.Null);

        Assert.InRange(watch.Elapsed.TotalSeconds, 1.2, 10); // 96 KB at 64 KB/s = 1.5 s.
    }

    [Fact]
    public async Task Local_destination_upload_is_limited()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("f.bin");
        await File.WriteAllBytesAsync(file, new byte[80 * 1024]);
        await using var destination = new LocalFolderDestination(new LocalFolderOptions { Path = temp.Combine("dst") }, maxUploadKBps: 64);

        var watch = Stopwatch.StartNew();
        await destination.UploadAsync(file, "f.bin", null, CancellationToken.None);

        Assert.True(watch.Elapsed.TotalSeconds >= 1.0, $"Upload took {watch.Elapsed.TotalSeconds:0.00}s");
        Assert.Equal(80 * 1024, new FileInfo(temp.Combine("dst", "f.bin")).Length);
    }

    [Fact]
    public void Wrap_without_limit_returns_the_same_stream()
    {
        using var inner = new MemoryStream();
        Assert.Same(inner, ThrottledStream.Wrap(inner, 0));
    }

    [Theory]
    [InlineData("22:00-06:00", 23, 0, 0)]
    [InlineData("22:00-06:00", 5, 59, 0)]
    [InlineData("22:00-06:00", 6, 0, 16 * 60)]
    [InlineData("22:00-06:00", 12, 0, 10 * 60)]
    [InlineData("01:00-05:30", 0, 30, 30)]
    [InlineData("01:00-05:30", 5, 30, (24 * 60) - 270)]
    public void Upload_window_delay(string window, int hour, int minute, int expectedMinutes)
    {
        var now = new DateTimeOffset(2026, 4, 10, hour, minute, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), UploadWindow.Parse(window)!.Delay(now, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("25:00-01:00")]
    [InlineData("10:00")]
    [InlineData("10:00-10:00")]
    public void Invalid_upload_windows_are_rejected(string text)
    {
        Assert.Throws<FormatException>(() => UploadWindow.Parse(text));
        Assert.Contains(Engine.BackupJobRunner.GetValidationErrors(new BackupJob { Schedule = { UploadWindow = text } }), e => e.Contains("Upload window"));
    }
}
