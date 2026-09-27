#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Konscious.Security.Cryptography;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Users;

namespace Wino.Services;

/// <summary>
/// The sync snapshot wire format: a 48-byte header (authenticated data) followed by AES-256-GCM
/// ciphertext and tag. The key is Argon2id over the account password or a sync passphrase with the
/// parameters written into the header, so any device with the secret can open it. The layout is
/// fixed by <see cref="SyncSnapshotFormat"/> and described in the API repository's
/// docs/sync-snapshot-format.md.
/// </summary>
public static class SyncSnapshotCryptography
{
    public const int KeyLength = 32;

    public sealed record Header(
        int FormatVersion,
        byte KeySource,
        byte KdfId,
        int MemoryKiB,
        int Iterations,
        int Parallelism,
        byte Flags,
        byte[] Salt,
        byte[] Nonce)
    {
        public bool IsCompressed => (Flags & SyncSnapshotFormat.FlagGzip) != 0;

        public SyncSnapshotKeyParameters ToKeyParameters() => new(KeySource, MemoryKiB, Iterations, Parallelism, Salt);
    }

    public static bool IsSnapshot(ReadOnlySpan<byte> content)
        => content.Length >= SyncSnapshotFormat.MagicLength && content[..SyncSnapshotFormat.MagicLength].SequenceEqual(SyncSnapshotFormat.Magic);

    public static byte[] CreateSalt()
    {
        var salt = new byte[SyncSnapshotFormat.SaltLength];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    /// <summary>
    /// A salt that is the same for the user on every device, so the derived key can be cached
    /// per device and still open snapshots made elsewhere. The salt is not secret; it only
    /// has to differ between users.
    /// </summary>
    public static byte[] CreateUserSalt(Guid userId)
        => SHA256.HashData(Encoding.UTF8.GetBytes($"Wino.SyncSnapshot.Salt.{userId:D}"))[..SyncSnapshotFormat.SaltLength];

    public static async Task<byte[]> DeriveKeyAsync(string secret, SyncSnapshotKeyParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var secretBytes = Encoding.UTF8.GetBytes(secret.Normalize(NormalizationForm.FormC));
        try
        {
            using var argon2 = new Argon2id(secretBytes)
            {
                Salt = parameters.Salt,
                MemorySize = parameters.MemoryKiB,
                Iterations = parameters.Iterations,
                DegreeOfParallelism = parameters.Parallelism,
            };

            cancellationToken.ThrowIfCancellationRequested();
            return await argon2.GetBytesAsync(KeyLength).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, SyncSnapshotKey key, bool compress = true)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Key.Length != KeyLength) throw new ArgumentException("Snapshot keys are 32 bytes.", nameof(key));
        if (key.Parameters.Salt.Length != SyncSnapshotFormat.SaltLength) throw new ArgumentException("Snapshot salts are 16 bytes.", nameof(key));

        var body = compress ? Compress(plaintext) : plaintext.ToArray();
        var nonce = new byte[SyncSnapshotFormat.NonceLength];
        RandomNumberGenerator.Fill(nonce);

        var payload = new byte[SyncSnapshotFormat.HeaderLength + body.Length + SyncSnapshotFormat.TagLength];
        var header = payload.AsSpan(0, SyncSnapshotFormat.HeaderLength);
        SyncSnapshotFormat.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[SyncSnapshotFormat.FormatVersionOffset..], SyncSnapshotFormat.CurrentFormatVersion);
        header[SyncSnapshotFormat.KeySourceOffset] = key.Parameters.KeySource;
        header[SyncSnapshotFormat.KdfIdOffset] = SyncSnapshotFormat.KdfArgon2id;
        BinaryPrimitives.WriteUInt32LittleEndian(header[SyncSnapshotFormat.MemoryKiBOffset..], (uint)key.Parameters.MemoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(header[SyncSnapshotFormat.IterationsOffset..], (uint)key.Parameters.Iterations);
        header[SyncSnapshotFormat.ParallelismOffset] = (byte)key.Parameters.Parallelism;
        header[SyncSnapshotFormat.FlagsOffset] = compress ? SyncSnapshotFormat.FlagGzip : (byte)0;
        key.Parameters.Salt.CopyTo(header[SyncSnapshotFormat.SaltOffset..]);
        nonce.CopyTo(header[SyncSnapshotFormat.NonceOffset..]);

        using var aes = new AesGcm(key.Key, SyncSnapshotFormat.TagLength);
        aes.Encrypt(
            nonce,
            body,
            payload.AsSpan(SyncSnapshotFormat.HeaderLength, body.Length),
            payload.AsSpan(SyncSnapshotFormat.HeaderLength + body.Length, SyncSnapshotFormat.TagLength),
            header);

        return payload;
    }

    public static Header ReadHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < SyncSnapshotFormat.MinPayloadLength || !IsSnapshot(payload))
        {
            throw new SyncSnapshotInvalidFileException("The content is not a Wino sync snapshot.");
        }

        var formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(payload[SyncSnapshotFormat.FormatVersionOffset..]);
        if (formatVersion is 0 or > SyncSnapshotFormat.CurrentFormatVersion)
        {
            throw new SyncSnapshotInvalidFileException($"Snapshot format {formatVersion} needs a newer Wino Mail.");
        }

        var kdfId = payload[SyncSnapshotFormat.KdfIdOffset];
        if (kdfId != SyncSnapshotFormat.KdfArgon2id)
        {
            throw new SyncSnapshotInvalidFileException($"Snapshot key derivation {kdfId} is not supported.");
        }

        var keySource = payload[SyncSnapshotFormat.KeySourceOffset];
        if (keySource is not (SyncSnapshotFormat.KeySourceAccountPassword or SyncSnapshotFormat.KeySourcePassphrase))
        {
            throw new SyncSnapshotInvalidFileException($"Snapshot key source {keySource} is not supported.");
        }

        var memoryKiB = BinaryPrimitives.ReadUInt32LittleEndian(payload[SyncSnapshotFormat.MemoryKiBOffset..]);
        var iterations = BinaryPrimitives.ReadUInt32LittleEndian(payload[SyncSnapshotFormat.IterationsOffset..]);
        var parallelism = payload[SyncSnapshotFormat.ParallelismOffset];
        if (memoryKiB is 0 or > int.MaxValue || iterations is 0 or > int.MaxValue || parallelism == 0)
        {
            throw new SyncSnapshotInvalidFileException("Snapshot key derivation parameters are invalid.");
        }

        return new Header(
            formatVersion,
            keySource,
            kdfId,
            (int)memoryKiB,
            (int)iterations,
            parallelism,
            payload[SyncSnapshotFormat.FlagsOffset],
            payload.Slice(SyncSnapshotFormat.SaltOffset, SyncSnapshotFormat.SaltLength).ToArray(),
            payload.Slice(SyncSnapshotFormat.NonceOffset, SyncSnapshotFormat.NonceLength).ToArray());
    }

    /// <summary>
    /// Opens the snapshot with a key derived for its header. A tag mismatch means the wrong
    /// secret, a previous password, or a damaged payload.
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key)
    {
        var header = ReadHeader(payload);
        var bodyLength = payload.Length - SyncSnapshotFormat.HeaderLength - SyncSnapshotFormat.TagLength;
        var body = new byte[bodyLength];

        try
        {
            using var aes = new AesGcm(key, SyncSnapshotFormat.TagLength);
            aes.Decrypt(
                header.Nonce,
                payload.Slice(SyncSnapshotFormat.HeaderLength, bodyLength),
                payload.Slice(SyncSnapshotFormat.HeaderLength + bodyLength, SyncSnapshotFormat.TagLength),
                body,
                payload[..SyncSnapshotFormat.HeaderLength]);
        }
        catch (CryptographicException ex)
        {
            throw new SyncSnapshotDecryptionException("The snapshot could not be unlocked.", ex);
        }

        return header.IsCompressed ? Decompress(body) : body;
    }

    private static byte[] Compress(ReadOnlySpan<byte> plaintext)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(plaintext);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] body)
    {
        try
        {
            using var input = new MemoryStream(body);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new SyncSnapshotDecryptionException("The snapshot content is damaged.", ex);
        }
    }
}
