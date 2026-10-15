using System.Security.Cryptography;
using System.Text;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Security;

/// <summary>
/// The installation's 32-byte protection key, stored in a file wrapped by a non-exportable AES-256-GCM key in the
/// Android Keystore (alias <see cref="KeystoreAlias"/>). <see cref="Wino.Services.Security.AesGcmEnvelopeSecretProtector"/>
/// needs the raw key bytes, so the Keystore key only wraps and unwraps them; it never leaves the Keystore.
/// </summary>
/// <remarks>
/// File layout (version 1, stored data, never changes): "WPKW", version byte 1, IV length byte (12), the IV chosen by
/// the Keystore, then the 32-byte key encrypted with AES-GCM and its 16-byte tag. The associated data is the UTF-8
/// "wino-protection-v1:" followed by the application identity.
/// Failures: no wrapped file reads as missing (null). A malformed file, a wrong tag, an unwrapped key of the wrong size,
/// or a file whose Keystore key is gone or invalidated is corrupt (<see cref="CryptographicException"/>).
/// A Keystore that cannot be loaded or used is unavailable (<see cref="PlatformNotSupportedException"/>), as the
/// <see cref="ISecretProtector"/> contract requires.
/// </remarks>
public sealed class AndroidKeystoreProtectionKeyProvider : ISecretProtectionKeyProvider
{
    public const string KeystoreAlias = "wino-protection-v1";
    private const string KeystoreProvider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int KeyLength = 32;
    private const int IvLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = 6;
    private const int FileLength = HeaderLength + IvLength + KeyLength + TagLength;
    private static readonly byte[] Magic = "WPKW"u8.ToArray();
    private static readonly Lock Gate = new();
    private readonly string _path;

    public AndroidKeystoreProtectionKeyProvider(string wrappedKeyPath, string applicationIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wrappedKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationIdentity);
        _path = Path.GetFullPath(wrappedKeyPath);
        ApplicationIdentity = applicationIdentity;
    }

    public string ApplicationIdentity { get; }

    public byte[]? ReadKey()
    {
        byte[] file;
        try { file = File.ReadAllBytes(_path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        if (file.Length != FileLength || !file.AsSpan(0, 4).SequenceEqual(Magic) || file[4] != 1 || file[5] != IvLength)
            throw new CryptographicException("The wrapped protection key file is invalid.");

        var wrappingKey = Keystore(keystore => keystore.GetKey(KeystoreAlias, null))
            ?? throw new CryptographicException("The Keystore key that wraps the protection key is missing.");
        byte[]? key = null;
        try
        {
            key = Keystore(_ =>
            {
                var cipher = Cipher.GetInstance(Transformation) ?? throw new ProviderException("AES-GCM is unavailable.");
                cipher.Init(Javax.Crypto.CipherMode.DecryptMode, wrappingKey, new GCMParameterSpec(TagLength * 8, file, HeaderLength, IvLength));
                cipher.UpdateAAD(AssociatedData());
                return cipher.DoFinal(file, HeaderLength + IvLength, KeyLength + TagLength);
            });
        }
        catch (AEADBadTagException error) { throw new CryptographicException("The wrapped protection key does not authenticate.", error); }
        catch (KeyPermanentlyInvalidatedException error) { throw new CryptographicException("The Keystore key that wraps the protection key was invalidated.", error); }
        catch (InvalidKeyException error) { throw new CryptographicException("The Keystore key cannot unwrap the protection key.", error); }

        if (key is { Length: KeyLength }) return key;
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException("The unwrapped protection key has an invalid length.");
    }

    public bool TryAddKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyLength) throw new ArgumentException("The protection key must be 32 bytes.", nameof(key));
        lock (Gate)
        {
            if (File.Exists(_path)) return false;

            var wrapped = Keystore(keystore =>
            {
                var wrappingKey = keystore.GetKey(KeystoreAlias, null) ?? GenerateWrappingKey();
                var cipher = Cipher.GetInstance(Transformation) ?? throw new ProviderException("AES-GCM is unavailable.");
                cipher.Init(Javax.Crypto.CipherMode.EncryptMode, wrappingKey);
                cipher.UpdateAAD(AssociatedData());
                var iv = cipher.GetIV();
                var sealedKey = cipher.DoFinal(key);
                if (iv is not { Length: IvLength } || sealedKey is not { Length: KeyLength + TagLength })
                    throw new ProviderException("The Keystore returned an unexpected AES-GCM layout.");
                var file = new byte[FileLength];
                Magic.CopyTo(file, 0);
                file[4] = 1;
                file[5] = IvLength;
                iv.CopyTo(file, HeaderLength);
                sealedKey.CopyTo(file, HeaderLength + IvLength);
                return file;
            });

            var folder = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("The protection key path has no folder.");
            Directory.CreateDirectory(folder);
            var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(wrapped);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(_path))
            {
                File.Delete(temporary);
                return false;
            }
            catch
            {
                File.Delete(temporary);
                throw;
            }
        }
    }

    private byte[] AssociatedData() => Encoding.UTF8.GetBytes(KeystoreAlias + ":" + ApplicationIdentity);

    private static IKey GenerateWrappingKey()
    {
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeystoreProvider)
            ?? throw new ProviderException("The Keystore key generator is unavailable.");
        using var builder = new KeyGenParameterSpec.Builder(KeystoreAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt);
        builder.SetBlockModes(KeyProperties.BlockModeGcm);
        builder.SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone);
        builder.SetKeySize(KeyLength * 8);
        builder.SetRandomizedEncryptionRequired(true);
        generator.Init(builder.Build());
        return generator.GenerateKey() ?? throw new ProviderException("The Keystore did not create a key.");
    }

    /// <summary>
    /// Runs <paramref name="operation"/> with the loaded Android Keystore. Keystore and provider failures become
    /// <see cref="PlatformNotSupportedException"/>; the corrupt-data exceptions the callers handle pass through.
    /// </summary>
    private static T Keystore<T>(Func<KeyStore, T> operation)
    {
        try
        {
            var keystore = KeyStore.GetInstance(KeystoreProvider) ?? throw new KeyStoreException("The Android Keystore is unavailable.");
            keystore.Load(null);
            return operation(keystore);
        }
        catch (Exception error) when (error is AEADBadTagException or KeyPermanentlyInvalidatedException or InvalidKeyException) { throw; }
        catch (Java.Lang.Exception error) { throw new PlatformNotSupportedException("The Android Keystore is unavailable.", error); }
    }
}
