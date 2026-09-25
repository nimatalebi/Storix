using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NT.Storix.Core.Processing;

/// <summary>
/// Streaming file encryption: AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC) with PBKDF2-SHA256 key derivation.
/// </summary>
/// <remarks>
/// File layout:
/// <code>
/// "STRX" | version (1 byte) | PBKDF2 iterations (int32 LE) | salt (16) | IV (16) | ciphertext ... | HMAC-SHA256 (32)
/// </code>
/// The HMAC covers the header and the ciphertext. Decryption verifies the HMAC before producing any output.
/// </remarks>
public static class AesFileEncryptor
{
    public const string FileExtension = ".aes";
    public const int DefaultIterations = 600_000;

    private static readonly byte[] Magic = "STRX"u8.ToArray();
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int IvSize = 16;
    private const int MacSize = 32;
    private const int HeaderSize = 4 + 1 + 4 + SaltSize + IvSize;
    private const int BufferSize = 1024 * 1024;

    public static async Task EncryptAsync(string inputPath, string outputPath, string password, CancellationToken cancellationToken, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var iv = RandomNumberGenerator.GetBytes(IvSize);
        var (encKey, macKey) = DeriveKeys(password, salt, iterations);

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[4] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5, 4), iterations);
        salt.CopyTo(header, 9);
        iv.CopyTo(header, 9 + SaltSize);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
        hmac.AppendData(header);

        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        await output.WriteAsync(header, cancellationToken);

        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        await using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
        await using (var macStream = new HashingWriteStream(output, hmac))
        await using (var crypto = new CryptoStream(macStream, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
        {
            await input.CopyToAsync(crypto, BufferSize, cancellationToken);
            await crypto.FlushFinalBlockAsync(cancellationToken);
        }

        await output.WriteAsync(hmac.GetHashAndReset(), cancellationToken);
    }

    /// <summary>Verifies the password and the integrity of an encrypted file without decrypting it.</summary>
    public static async Task VerifyAsync(string encryptedPath, string password, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await ReadAndAuthenticateAsync(input, password, cancellationToken);
    }

    public static async Task DecryptAsync(string encryptedPath, string outputPath, string password, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var (encKey, iv, cipherLength) = await ReadAndAuthenticateAsync(input, password, cancellationToken);

        input.Position = HeaderSize;
        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        await using var bounded = new BoundedReadStream(input, cipherLength);
        await using var crypto = new CryptoStream(bounded, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
        await crypto.CopyToAsync(output, BufferSize, cancellationToken);
    }

    public static bool IsEncryptedFile(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        return stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) == 4 && magic.SequenceEqual(Magic);
    }

    private static async Task<(byte[] EncKey, byte[] Iv, long CipherLength)> ReadAndAuthenticateAsync(FileStream input, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var header = new byte[HeaderSize];
        await input.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic) || header[4] != Version)
        {
            throw new InvalidDataException("The file is not a Storix encrypted archive or its version is not supported.");
        }

        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(5, 4));
        var salt = header.AsSpan(9, SaltSize).ToArray();
        var iv = header.AsSpan(9 + SaltSize, IvSize).ToArray();
        var cipherLength = input.Length - HeaderSize - MacSize;
        if (cipherLength < 0 || cipherLength % 16 != 0)
        {
            throw new InvalidDataException("The encrypted file is truncated.");
        }

        var (encKey, macKey) = DeriveKeys(password, salt, iterations);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
        hmac.AppendData(header);

        var buffer = new byte[BufferSize];
        var remaining = cipherLength;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            hmac.AppendData(buffer, 0, read);
            remaining -= read;
        }

        var expected = new byte[MacSize];
        await input.ReadExactlyAsync(expected, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(expected, hmac.GetHashAndReset()))
        {
            throw new CryptographicException("Wrong password or the file has been modified/corrupted.");
        }

        return (encKey, iv, cipherLength);
    }

    private static (byte[] EncKey, byte[] MacKey) DeriveKeys(string password, byte[] salt, int iterations)
    {
        var keys = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 64);
        return (keys[..32], keys[32..]);
    }

    /// <summary>Pass-through write stream that feeds everything it writes into an HMAC.</summary>
    private sealed class HashingWriteStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Read-only view over the next <c>length</c> bytes of another stream.</summary>
    private sealed class BoundedReadStream(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
