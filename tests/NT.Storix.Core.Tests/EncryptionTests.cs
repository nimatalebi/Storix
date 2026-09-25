using System.Security.Cryptography;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Tests;

public class EncryptionTests
{
    private const int FastIterations = 1_000;

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(3 * 1024 * 1024 + 7)]
    public async Task Encrypt_then_decrypt_returns_original(int size)
    {
        using var temp = new TempDirectory();
        var data = RandomNumberGenerator.GetBytes(size);
        var plain = temp.Combine("plain.bin");
        await File.WriteAllBytesAsync(plain, data);

        await AesFileEncryptor.EncryptAsync(plain, temp.Combine("enc.aes"), "p@ss", CancellationToken.None, FastIterations);
        Assert.True(AesFileEncryptor.IsEncryptedFile(temp.Combine("enc.aes")));

        await AesFileEncryptor.VerifyAsync(temp.Combine("enc.aes"), "p@ss", CancellationToken.None);
        await AesFileEncryptor.DecryptAsync(temp.Combine("enc.aes"), temp.Combine("out.bin"), "p@ss", CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(temp.Combine("out.bin")));
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        using var temp = new TempDirectory();
        var plain = temp.WriteFile("plain.txt", "secret data");
        await AesFileEncryptor.EncryptAsync(plain, temp.Combine("enc.aes"), "right", CancellationToken.None, FastIterations);

        await Assert.ThrowsAsync<CryptographicException>(() =>
            AesFileEncryptor.DecryptAsync(temp.Combine("enc.aes"), temp.Combine("out.txt"), "wrong", CancellationToken.None));
        Assert.False(File.Exists(temp.Combine("out.txt")));
    }

    [Fact]
    public async Task Tampered_file_is_rejected()
    {
        using var temp = new TempDirectory();
        var plain = temp.WriteFile("plain.txt", new string('x', 10_000));
        var encrypted = temp.Combine("enc.aes");
        await AesFileEncryptor.EncryptAsync(plain, encrypted, "pw", CancellationToken.None, FastIterations);

        var bytes = await File.ReadAllBytesAsync(encrypted);
        bytes[100] ^= 0xFF;
        await File.WriteAllBytesAsync(encrypted, bytes);

        await Assert.ThrowsAsync<CryptographicException>(() => AesFileEncryptor.VerifyAsync(encrypted, "pw", CancellationToken.None));
    }
}
