using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Wino.Agent.Runtime.Ports;

namespace Wino.Agent.Runtime.Brain;

/// <summary>
/// The brain sync blob (plan section 4.10): a 48-byte plain header (magic <c>WBRN01</c>, format version, key source,
/// KDF id, Argon2id parameters, salt, nonce; the same layout as the sync snapshot) that is authenticated data for the
/// AES-256-GCM ciphertext of gzip(JSON) that follows. The key is Argon2id over the user's brain password with the
/// parameters in the header, so any device with the password can open the blob. Only the derived key is ever kept
/// (<see cref="BrainSync"/>), never the password.
/// </summary>
public sealed class BrainCrypto : IBrainBlobCodec
{
    public const int KeyLength = 32;
    public const int MagicLength = 6;
    public const ushort CurrentFormatVersion = 1;
    public const byte KeySourcePassphrase = 2;
    public const byte KdfArgon2id = 1;
    public const byte FlagGzip = 0x01;
    public const int FormatVersionOffset = 6;
    public const int KeySourceOffset = 8;
    public const int KdfIdOffset = 9;
    public const int MemoryKiBOffset = 10;
    public const int IterationsOffset = 14;
    public const int ParallelismOffset = 18;
    public const int FlagsOffset = 19;
    public const int SaltOffset = 20;
    public const int SaltLength = 16;
    public const int NonceOffset = 36;
    public const int NonceLength = 12;
    public const int HeaderLength = 48;
    public const int TagLength = 16;
    public const int MinBlobLength = HeaderLength + TagLength + 1;

    /// <summary>The plain document is capped here; the server caps the blob at 512 KiB.</summary>
    public const int MaxPlainBytes = 256 * 1024;

    // RFC 9106 second recommended profile, the same as the sync snapshot's.
    public const int DefaultMemoryKiB = 65536;
    public const int DefaultIterations = 3;
    public const int DefaultParallelism = 4;

    public static ReadOnlySpan<byte> Magic => "WBRN01"u8;

    public BrainCrypto(BrainKeyParameters parameters)
    {
        if (parameters.Salt.Length != SaltLength) throw new ArgumentException("Brain salts are 16 bytes.", nameof(parameters));
        Parameters = parameters;
    }

    /// <summary>The parameters written into every blob this codec produces.</summary>
    public BrainKeyParameters Parameters { get; }

    public static BrainKeyParameters CreateParameters()
    {
        var salt = new byte[SaltLength];
        RandomNumberGenerator.Fill(salt);
        return new BrainKeyParameters(DefaultMemoryKiB, DefaultIterations, DefaultParallelism, salt);
    }

    public static bool IsBrainBlob(ReadOnlySpan<byte> blob) => blob.Length >= MagicLength && blob[..MagicLength].SequenceEqual(Magic);

    /// <summary>Argon2id over the password (NFC) with <paramref name="parameters"/>; the password bytes are wiped afterwards.</summary>
    public static byte[] DeriveKey(string password, BrainKeyParameters parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var secret = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        try
        {
            using var argon2 = new Argon2id(secret)
            {
                Salt = parameters.Salt,
                MemorySize = parameters.MemoryKiB,
                Iterations = parameters.Iterations,
                DegreeOfParallelism = parameters.Parallelism,
            };
            return argon2.GetBytes(KeyLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static Task<byte[]> DeriveKeyAsync(string password, BrainKeyParameters parameters, CancellationToken ct)
        => Task.Run(() => DeriveKey(password, parameters), ct);

    public static BrainBlobHeader ReadHeader(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < MinBlobLength || !IsBrainBlob(blob)) throw new BrainBlobInvalidException("The content is not a Wino brain blob.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(blob[FormatVersionOffset..]);
        if (version is 0 or > CurrentFormatVersion) throw new BrainBlobInvalidException($"Brain format {version} needs a newer Wino Mail.");
        if (blob[KeySourceOffset] != KeySourcePassphrase) throw new BrainBlobInvalidException("Brain key source is not supported.");
        if (blob[KdfIdOffset] != KdfArgon2id) throw new BrainBlobInvalidException("Brain key derivation is not supported.");
        var memory = BinaryPrimitives.ReadUInt32LittleEndian(blob[MemoryKiBOffset..]);
        var iterations = BinaryPrimitives.ReadUInt32LittleEndian(blob[IterationsOffset..]);
        var parallelism = blob[ParallelismOffset];
        if (memory is 0 or > int.MaxValue || iterations is 0 or > int.MaxValue || parallelism == 0) throw new BrainBlobInvalidException("Brain key derivation parameters are invalid.");
        return new BrainBlobHeader(version, (blob[FlagsOffset] & FlagGzip) != 0,
            new BrainKeyParameters((int)memory, (int)iterations, parallelism, blob.Slice(SaltOffset, SaltLength).ToArray()),
            blob.Slice(NonceOffset, NonceLength).ToArray());
    }

    public byte[] Encrypt(BrainDocument document, byte[] key)
    {
        if (key.Length != KeyLength) throw new ArgumentException("Brain keys are 32 bytes.", nameof(key));
        var plain = JsonSerializer.SerializeToUtf8Bytes(document, BrainJsonContext.Default.BrainDocument);
        if (plain.Length > MaxPlainBytes) throw new BrainBlobTooLargeException(plain.Length);
        var body = Compress(plain);
        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);

        var blob = new byte[HeaderLength + body.Length + TagLength];
        var header = blob.AsSpan(0, HeaderLength);
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[FormatVersionOffset..], CurrentFormatVersion);
        header[KeySourceOffset] = KeySourcePassphrase;
        header[KdfIdOffset] = KdfArgon2id;
        BinaryPrimitives.WriteUInt32LittleEndian(header[MemoryKiBOffset..], (uint)Parameters.MemoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(header[IterationsOffset..], (uint)Parameters.Iterations);
        header[ParallelismOffset] = (byte)Parameters.Parallelism;
        header[FlagsOffset] = FlagGzip;
        Parameters.Salt.CopyTo(header[SaltOffset..]);
        nonce.CopyTo(header[NonceOffset..]);

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, body, blob.AsSpan(HeaderLength, body.Length), blob.AsSpan(HeaderLength + body.Length, TagLength), header);
        return blob;
    }

    /// <summary>Decrypts with <paramref name="key"/>; a wrong key (GCM tag failure) is <see cref="BrainKeyMismatchException"/>.</summary>
    public BrainDocument Decrypt(byte[] blob, byte[] key) => DecryptBlob(blob, key);

    public static BrainDocument DecryptBlob(byte[] blob, byte[] key)
    {
        var header = ReadHeader(blob);
        var bodyLength = blob.Length - HeaderLength - TagLength;
        var body = new byte[bodyLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(header.Nonce, blob.AsSpan(HeaderLength, bodyLength), blob.AsSpan(HeaderLength + bodyLength, TagLength), body, blob.AsSpan(0, HeaderLength));
        }
        catch (CryptographicException ex)
        {
            throw new BrainKeyMismatchException(ex);
        }

        var plain = header.IsCompressed ? Decompress(body) : body;
        try
        {
            return JsonSerializer.Deserialize(plain, BrainJsonContext.Default.BrainDocument) ?? throw new BrainBlobInvalidException("The brain blob holds no document.");
        }
        catch (JsonException ex)
        {
            throw new BrainBlobInvalidException("The brain blob holds no readable document.", ex);
        }
    }

    private static byte[] Compress(byte[] plain)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(plain);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] body)
    {
        using var input = new MemoryStream(body);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>Argon2id parameters and the salt; written into the blob header so another device can derive the key from the password.</summary>
public sealed record BrainKeyParameters(int MemoryKiB, int Iterations, int Parallelism, byte[] Salt)
{
    public bool SameAs(BrainKeyParameters other) => MemoryKiB == other.MemoryKiB && Iterations == other.Iterations && Parallelism == other.Parallelism && Salt.AsSpan().SequenceEqual(other.Salt);
}

public sealed record BrainBlobHeader(int FormatVersion, bool IsCompressed, BrainKeyParameters Parameters, byte[] Nonce);

public class BrainBlobInvalidException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The blob was encrypted with another password (the GCM tag did not verify).</summary>
public sealed class BrainKeyMismatchException(Exception? inner = null) : Exception("The brain blob was encrypted with another password.", inner);

public sealed class BrainBlobTooLargeException(int plainBytes) : Exception($"The brain document is {plainBytes} bytes; the sync cap is {BrainCrypto.MaxPlainBytes}.")
{
    public int PlainBytes { get; } = plainBytes;
}
