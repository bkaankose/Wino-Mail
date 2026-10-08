using System;
using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Security;

[SupportedOSPlatform("macos")]
public sealed class MacOSSecretProtector(MacOSKeychainStore keychain) : ISecretProtector
{
    private const string KeyScope = "protection";
    private const string KeyAccount = "aes256-gcm-v1";
    private const int HeaderLength = 9;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaximumPlaintextLength = 16 * 1024 * 1024;

    /// <summary>
    /// Call only after verifying that the installation contains no existing database or protected artifacts.
    /// The guard must come from the host's pre-initialization filesystem inspection.
    /// Existing keys are preserved, including during a concurrent first initialization.
    /// </summary>
    public void ProvisionNewInstallationKey(bool verifiedNewInstallation)
    {
        if (!verifiedNewInstallation)
            throw new InvalidOperationException("Protection key provisioning requires a verified new installation.");
        var key = RandomNumberGenerator.GetBytes(32);
        try { keychain.TryAdd(KeyScope, KeyAccount, key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public byte[] Protect(byte[] data, byte[] context)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(context);
        if (data.Length > MaximumPlaintextLength) throw new ArgumentOutOfRangeException(nameof(data));
        var key = ReadKey();
        var envelope = new byte[HeaderLength + NonceLength + TagLength + data.Length];
        "WMKP"u8.CopyTo(envelope);
        envelope[4] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(5, 4), data.Length);
        var nonce = envelope.AsSpan(HeaderLength, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        var aad = CreateAssociatedData(envelope.AsSpan(0, HeaderLength), context);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, data, envelope.AsSpan(HeaderLength + NonceLength + TagLength),
                envelope.AsSpan(HeaderLength + NonceLength, TagLength), aad);
            return envelope;
        }
        catch { CryptographicOperations.ZeroMemory(envelope); throw; }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(aad); }
    }

    public byte[] Unprotect(byte[] data, byte[] context)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(context);
        if (data.Length < HeaderLength + NonceLength + TagLength || !data.AsSpan(0, 4).SequenceEqual("WMKP"u8) || data[4] != 1)
            throw new CryptographicException("Invalid protected credential envelope.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(5, 4));
        if (length < 0 || length > MaximumPlaintextLength || length != data.Length - HeaderLength - NonceLength - TagLength)
            throw new CryptographicException("Invalid protected credential envelope length.");
        var key = ReadKey();
        var plaintext = new byte[length];
        var aad = CreateAssociatedData(data.AsSpan(0, HeaderLength), context);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(data.AsSpan(HeaderLength, NonceLength), data.AsSpan(HeaderLength + NonceLength + TagLength),
                data.AsSpan(HeaderLength + NonceLength, TagLength), plaintext, aad);
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(aad); }
    }

    private byte[] ReadKey()
    {
        var key = keychain.Read(KeyScope, KeyAccount) ?? throw new MacOSCredentialMissingException();
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException("The Keychain protection key has an invalid representation.");
    }

    private byte[] CreateAssociatedData(ReadOnlySpan<byte> header, byte[] context)
    {
        var identity = Encoding.UTF8.GetBytes(keychain.ApplicationIdentity);
        var aad = new byte[checked(header.Length + 8 + identity.Length + context.Length)];
        header.CopyTo(aad);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(header.Length, 4), identity.Length);
        identity.CopyTo(aad.AsSpan(header.Length + 4));
        var offset = header.Length + 4 + identity.Length;
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(offset, 4), context.Length);
        context.CopyTo(aad.AsSpan(offset + 4));
        return aad;
    }
}
