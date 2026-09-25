using System.Diagnostics;

namespace NT.Storix.Core.Processing;

/// <summary>Read-only stream wrapper that limits throughput (bandwidth limit for uploads).</summary>
public sealed class ThrottledStream(Stream inner, long bytesPerSecond, bool leaveOpen = false) : Stream
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _transferred;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, Math.Min(count, ChunkSize));
        var wait = Account(read);
        if (wait > TimeSpan.Zero)
        {
            Thread.Sleep(wait);
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, ChunkSize)], cancellationToken);
        var wait = Account(read);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Small reads keep the rate smooth for slow limits.</summary>
    private int ChunkSize => (int)Math.Clamp(bytesPerSecond / 4, 4096, 1024 * 1024);

    private TimeSpan Account(int read)
    {
        if (read <= 0 || bytesPerSecond <= 0)
        {
            return TimeSpan.Zero;
        }

        _transferred += read;
        var expected = TimeSpan.FromSeconds((double)_transferred / bytesPerSecond);
        var wait = expected - _clock.Elapsed;
        return wait > TimeSpan.FromMilliseconds(1) ? wait : TimeSpan.Zero;
    }

    /// <summary>Wraps <paramref name="stream"/> when a limit is set (KB/s), otherwise returns it unchanged.</summary>
    public static Stream Wrap(Stream stream, int kilobytesPerSecond) =>
        kilobytesPerSecond > 0 ? new ThrottledStream(stream, kilobytesPerSecond * 1024L) : stream;
}
