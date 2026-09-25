using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NT.Storix.Core.Processing;
using ZstdSharp;

namespace NT.Storix.Core.Dedup;

public sealed class DedupChunk
{
    /// <summary>Content id: HMAC-SHA256 (encrypted repositories) or SHA-256 of the plain chunk, 32 hex characters.</summary>
    public string Id { get; set; } = string.Empty;

    public string Pack { get; set; } = string.Empty;

    public long Offset { get; set; }

    /// <summary>Stored (compressed, encrypted) length in the pack.</summary>
    public int Length { get; set; }

    /// <summary>Plain length.</summary>
    public int Size { get; set; }
}

public sealed class DedupFile
{
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTimeOffset Modified { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Indexes into <see cref="DedupSnapshot.Chunks"/>, in file order.</summary>
    public List<int> Chunks { get; set; } = [];
}

/// <summary>
/// One deduplicated backup: the file list and where each chunk is stored, saved as <c>{name}.snap</c>.
/// File layout: <c>"STXS" | version (1) | salt length (1) | salt | payload</c>. The payload is gzip JSON, sealed like
/// a chunk (AES-256-GCM with the key derived from the password and the salt) when the salt is not empty.
/// </summary>
public sealed class DedupSnapshot
{
    public const string FormatName = "storix-snapshot";
    public const int CurrentVersion = 1;
    public const string Extension = ".snap";
    private static readonly byte[] Magic = "STXS"u8.ToArray();

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<DedupChunk> Chunks { get; set; } = [];

    public List<DedupFile> Files { get; set; } = [];

    public IEnumerable<string> Packs => Chunks.Select(c => c.Pack).Distinct(StringComparer.Ordinal);

    public async Task WriteAsync(string path, DedupKeys keys, CancellationToken cancellationToken)
    {
        using var json = new MemoryStream();
        await using (var gzip = new GZipStream(json, CompressionLevel.Optimal, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(gzip, this, StorixJson.Options, cancellationToken);
        }

        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        file.Write(Magic);
        file.WriteByte(CurrentVersion);
        file.WriteByte((byte)keys.Salt.Length);
        file.Write(keys.Salt);
        file.Write(keys.SealRaw(json.ToArray()));
    }

    /// <summary>The key salt of a snapshot file (empty for unencrypted repositories).</summary>
    public static byte[] ReadSalt(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[6];
        if (file.ReadAtLeast(header, 6, throwOnEndOfStream: false) != 6 || !header[..4].SequenceEqual(Magic) || header[4] != CurrentVersion)
        {
            throw new InvalidDataException("Not a Storix snapshot (or a newer version).");
        }

        var salt = new byte[header[5]];
        file.ReadExactly(salt);
        return salt;
    }

    /// <param name="keysForSalt">Returns the keys for a salt (derived from the password), or null when no password is known.</param>
    public static async Task<DedupSnapshot> ReadAsync(string path, Func<byte[], DedupKeys?> keysForSalt, CancellationToken cancellationToken)
    {
        var salt = ReadSalt(path);
        var keys = salt.Length == 0 ? DedupKeys.Plain : keysForSalt(salt) ?? throw new UnauthorizedAccessException("This backup is encrypted. Enter the encryption password.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var payload = keys.OpenRaw(bytes.AsSpan(6 + salt.Length));

        await using var gzip = new GZipStream(new MemoryStream(payload), CompressionMode.Decompress);
        var snapshot = await JsonSerializer.DeserializeAsync<DedupSnapshot>(gzip, StorixJson.Options, cancellationToken);
        return snapshot is { Format: FormatName } ? snapshot : throw new InvalidDataException("Invalid Storix snapshot.");
    }

    public BackupIndex ToIndex(string name) => new()
    {
        Archive = name,
        CreatedAt = CreatedAt,
        Entries = Files.Select(f => new IndexEntry { Path = f.Path, Size = f.Size, Modified = f.Modified }).ToList(),
    };
}

/// <summary>
/// Chunk ids and encryption of a deduplicated repository. Blob layout: nonce (12) | tag (16) | AES-256-GCM(zstd(chunk));
/// unencrypted repositories store zstd(chunk) and use SHA-256 ids.
/// </summary>
public sealed class DedupKeys
{
    private static readonly byte[] Context = "NT.Storix.Dedup.v1"u8.ToArray();
    private readonly byte[]? _encryptionKey;
    private readonly byte[]? _macKey;

    private DedupKeys(byte[] salt, byte[]? encryptionKey, byte[]? macKey) => (Salt, _encryptionKey, _macKey) = (salt, encryptionKey, macKey);

    public static DedupKeys Plain { get; } = new([], null, null);

    public byte[] Salt { get; }

    public bool Encrypted => _encryptionKey is not null;

    /// <summary>Derives the keys from the job secret: PBKDF2-SHA256 (600,000 iterations), then HKDF per purpose.</summary>
    public static DedupKeys Derive(string secret, byte[] salt)
    {
        var master = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, 600_000, HashAlgorithmName.SHA256, 32);
        var encryption = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt, [.. Context, 1]);
        var mac = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt, [.. Context, 2]);
        CryptographicOperations.ZeroMemory(master);
        return new DedupKeys(salt, encryption, mac);
    }

    public string ChunkId(ReadOnlySpan<byte> chunk) =>
        Convert.ToHexStringLower(_macKey is null ? SHA256.HashData(chunk) : HMACSHA256.HashData(_macKey, chunk))[..32];

    /// <summary>Compresses (zstd) and encrypts a chunk.</summary>
    public byte[] Seal(ReadOnlySpan<byte> chunk)
    {
        using var compressor = new Compressor(3);
        return SealRaw(compressor.Wrap(chunk));
    }

    /// <exception cref="CryptographicException">Wrong key or corrupted chunk.</exception>
    public byte[] Open(ReadOnlySpan<byte> blob, int size)
    {
        using var decompressor = new Decompressor();
        var plain = decompressor.Unwrap(OpenRaw(blob)).ToArray();
        return plain.Length == size ? plain : throw new InvalidDataException("Chunk has an unexpected size.");
    }

    internal byte[] SealRaw(ReadOnlySpan<byte> data)
    {
        if (_encryptionKey is null)
        {
            return data.ToArray();
        }

        var blob = new byte[28 + data.Length];
        RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Encrypt(blob.AsSpan(0, 12), data, blob.AsSpan(28), blob.AsSpan(12, 16), Context);
        return blob;
    }

    internal byte[] OpenRaw(ReadOnlySpan<byte> blob)
    {
        if (_encryptionKey is null)
        {
            return blob.ToArray();
        }

        if (blob.Length < 28)
        {
            throw new CryptographicException("Corrupted data.");
        }

        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Decrypt(blob[..12], blob[28..], blob.Slice(12, 16), plain, Context);
        return plain;
    }
}
