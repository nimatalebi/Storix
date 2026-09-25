namespace NT.Storix.Core.Processing;

internal static class StreamCopy
{
    public const int BufferSize = 1024 * 1024;

    /// <summary>Copies a stream and reports the running total (starting at <paramref name="startOffset"/>).</summary>
    public static async Task CopyAsync(Stream input, Stream output, IProgress<long>? progress, CancellationToken cancellationToken, long startOffset = 0)
    {
        var buffer = new byte[BufferSize];
        var total = startOffset;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            progress?.Report(total);
        }
    }
}
