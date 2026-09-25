using System.Buffers.Binary;
using System.Security.Cryptography;
using NT.Storix.Core.Security;

namespace NT.Storix.Core.Processing;

/// <summary>
/// Streaming file encryption: AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC).
/// </summary>
/// <remarks>
/// Version 1 (password): key from PBKDF2-SHA256.
/// <code>
/// "STRX" | 1 | PBKDF2 iterations (int32 LE) | salt (16) | IV (16) | ciphertext ... | HMAC-SHA256 (32)
/// </code>
/// Version 2 (public key): a random 512-bit data key is wrapped with RSA-OAEP-SHA256, so only the holder of the
/// private key can decrypt. The machine that makes the backups never needs the private key.
/// <code>
/// "STRX" | 2 | key id = SHA-256 of the public key (32) | wrapped key length (int32 LE) | wrapped key | IV (16) | ciphertext ... | HMAC-SHA256 (32)
/// </code>
/// The HMAC covers the header and the ciphertext. Decryption verifies the HMAC before producing any output.
/// Both versions are always readable.
/// </remarks>
public static class AesFileEncryptor
{
    public const string FileExtension = ".aes";
    public const int DefaultIterations = 600_000;

    private static readonly byte[] Magic = "STRX"u8.ToArray();
    private const byte PasswordVersion = 1;
    private const byte PublicKeyVersion = 2;
    private const int SaltSize = 16;
    private const int IvSize = 16;
    private const int MacSize = 32;
    private const int KeyIdSize = 32;
    private const int PasswordHeaderSize = 4 + 1 + 4 + SaltSize + IvSize;
    private const int BufferSize = 1024 * 1024;

    /// <summary>Encrypts with a password (format version 1).</summary>
    public static async Task EncryptAsync(string inputPath, string outputPath, string password, CancellationToken cancellationToken, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (PrivateKeySecret.IsPublicKey(password))
        {
            await EncryptWithPublicKeyAsync(inputPath, outputPath, PrivateKeySecret.PublicKeyPem(password), cancellationToken);
            return;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var iv = RandomNumberGenerator.GetBytes(IvSize);
        var (encKey, macKey) = DeriveKeys(password, salt, iterations);

        var header = new byte[PasswordHeaderSize];
        Magic.CopyTo(header, 0);
        header[4] = PasswordVersion;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5, 4), iterations);
        salt.CopyTo(header, 9);
        iv.CopyTo(header, 9 + SaltSize);

        await EncryptCoreAsync(inputPath, outputPath, header, encKey, macKey, iv, cancellationToken);
    }

    /// <summary>Encrypts for the holder of the private key matching <paramref name="publicKeyPem"/> (format version 2).</summary>
    public static async Task EncryptWithPublicKeyAsync(string inputPath, string outputPath, string publicKeyPem, CancellationToken cancellationToken)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);

        var dataKey = RandomNumberGenerator.GetBytes(64);
        var iv = RandomNumberGenerator.GetBytes(IvSize);
        var wrapped = rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
        var keyId = KeyId(rsa);

        var header = new byte[4 + 1 + KeyIdSize + 4 + wrapped.Length + IvSize];
        Magic.CopyTo(header, 0);
        header[4] = PublicKeyVersion;
        keyId.CopyTo(header, 5);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5 + KeyIdSize, 4), wrapped.Length);
        wrapped.CopyTo(header, 5 + KeyIdSize + 4);
        iv.CopyTo(header, header.Length - IvSize);

        await EncryptCoreAsync(inputPath, outputPath, header, dataKey[..32], dataKey[32..], iv, cancellationToken);
        CryptographicOperations.ZeroMemory(dataKey);
    }

    /// <summary>Verifies the password (or private key) and the integrity of an encrypted file without decrypting it.</summary>
    public static async Task VerifyAsync(string encryptedPath, string password, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await ReadAndAuthenticateAsync(input, password, cancellationToken);
    }

    public static async Task DecryptAsync(string encryptedPath, string outputPath, string password, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var (encKey, iv, headerLength, cipherLength) = await ReadAndAuthenticateAsync(input, password, cancellationToken);

        input.Position = headerLength;
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

    /// <summary>True when the file was encrypted with a public key (needs the private key to restore).</summary>
    public static bool UsesPublicKey(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[5];
        return stream.ReadAtLeast(header, 5, throwOnEndOfStream: false) == 5 && header[..4].SequenceEqual(Magic) && header[4] == PublicKeyVersion;
    }

    /// <summary>Identifier of an RSA key: SHA-256 of its public key (SubjectPublicKeyInfo).</summary>
    public static byte[] KeyId(RSA rsa) => SHA256.HashData(rsa.ExportSubjectPublicKeyInfo());

    private static async Task EncryptCoreAsync(string inputPath, string outputPath, byte[] header, byte[] encKey, byte[] macKey, byte[] iv, CancellationToken cancellationToken)
    {
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

    private static async Task<(byte[] EncKey, byte[] Iv, int HeaderLength, long CipherLength)> ReadAndAuthenticateAsync(FileStream input, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var prefix = new byte[5];
        await input.ReadExactlyAsync(prefix, cancellationToken);
        if (!prefix.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The file is not a Storix encrypted archive.");
        }

        byte[] header;
        byte[] encKey;
        byte[] macKey;
        byte[] iv;
        switch (prefix[4])
        {
            case PasswordVersion:
            {
                header = new byte[PasswordHeaderSize];
                prefix.CopyTo(header, 0);
                await input.ReadExactlyAsync(header.AsMemory(5), cancellationToken);
                var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(5, 4));
                var salt = header.AsSpan(9, SaltSize).ToArray();
                iv = header.AsSpan(9 + SaltSize, IvSize).ToArray();
                (encKey, macKey) = DeriveKeys(password, salt, iterations);
                break;
            }

            case PublicKeyVersion:
            {
                var fixedPart = new byte[KeyIdSize + 4];
                await input.ReadExactlyAsync(fixedPart, cancellationToken);
                var wrappedLength = BinaryPrimitives.ReadInt32LittleEndian(fixedPart.AsSpan(KeyIdSize, 4));
                if (wrappedLength is <= 0 or > 8192)
                {
                    throw new InvalidDataException("The encrypted file header is corrupted.");
                }

                var rest = new byte[wrappedLength + IvSize];
                await input.ReadExactlyAsync(rest, cancellationToken);
                header = [.. prefix, .. fixedPart, .. rest];
                iv = rest[wrappedLength..];

                using var rsa = PrivateKeySecret.LoadPrivateKey(password)
                    ?? throw new CryptographicException("This backup was encrypted with a public key. Select the private key file to restore it.");
                if (!KeyId(rsa).AsSpan().SequenceEqual(fixedPart.AsSpan(0, KeyIdSize)))
                {
                    throw new CryptographicException("This backup was encrypted with a different key pair.");
                }

                var dataKey = rsa.Decrypt(rest[..wrappedLength], RSAEncryptionPadding.OaepSHA256);
                (encKey, macKey) = (dataKey[..32], dataKey[32..]);
                break;
            }

            default:
                throw new InvalidDataException($"Unsupported Storix encryption format version {prefix[4]}.");
        }

        var cipherLength = input.Length - header.Length - MacSize;
        if (cipherLength < 0 || cipherLength % 16 != 0)
        {
            throw new InvalidDataException("The encrypted file is truncated.");
        }

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

        return (encKey, iv, header.Length, cipherLength);
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
