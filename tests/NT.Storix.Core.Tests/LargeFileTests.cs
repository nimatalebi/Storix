using System.Security.Cryptography;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Tests;

/// <summary>Slow tests with multi-gigabyte files. Opt-in: set <c>STORIX_LARGE_TESTS=1</c>.</summary>
public sealed class LargeFactAttribute : FactAttribute
{
    public LargeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STORIX_LARGE_TESTS") != "1")
        {
            Skip = "Large-file test: set STORIX_LARGE_TESTS=1 (needs about 3x the file size of free disk space).";
        }
    }
}

public class LargeFileTests
{
    [LargeFact]
    public Task File_larger_than_4GB_round_trips_through_zip64_and_encryption() => RoundTripAsync(ArchiveCompression.Fastest);

    [LargeFact]
    public Task File_larger_than_4GB_round_trips_through_zstd() => RoundTripAsync(ArchiveCompression.Zstd);

    private static async Task RoundTripAsync(ArchiveCompression compression)
    {
        const long size = 4L * 1024 * 1024 * 1024 + 123_457; // Just over the classic ZIP 4 GB limit.
        using var temp = new TempDirectory();
        var source = temp.Combine("big.bin");

        // Sparse file with a few random markers so corruption would be detected.
        var markers = new[] { 0L, size / 3, size / 2, size - 4096 };
        await using (var stream = new FileStream(source, FileMode.CreateNew))
        {
            stream.SetLength(size);
            foreach (var offset in markers)
            {
                stream.Position = offset;
                stream.Write(RandomNumberGenerator.GetBytes(4096));
            }
        }

        var sourceHash = await Checksum.Sha256Async(source, CancellationToken.None);
        var zip = temp.Combine("big.zip");
        var archive = await ArchiveBuilder.CreateAsync([new ArchiveEntry(source, "big.bin")], zip, compression, null, CancellationToken.None);
        await ArchiveBuilder.VerifyAsync(zip, archive.EntryCount, CancellationToken.None);

        var encrypted = zip + AesFileEncryptor.FileExtension;
        await AesFileEncryptor.EncryptAsync(zip, encrypted, "pw", CancellationToken.None, iterations: 1000);
        File.Delete(zip);
        File.Delete(source);

        await RestoreService.RestoreFromFileAsync(encrypted, new RestoreRequest(temp.Combine("restored"), "pw"), null, CancellationToken.None);
        var restored = temp.Combine("restored", "big.bin");
        Assert.Equal(size, new FileInfo(restored).Length);
        Assert.Equal(sourceHash, await Checksum.Sha256Async(restored, CancellationToken.None));
    }
}
