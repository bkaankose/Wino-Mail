using System.Security.Cryptography;
using Wino.Core.Domain.Interfaces;

namespace Wino.Hosting;

/// <summary>
/// The protection key as a plain file inside the host's data root. The apps never do this: they keep the key in
/// DPAPI, the Keychain or the Android Keystore. A host holds only throwaway lab data inside its own data root, so an
/// unprotected key file there is accepted here and nowhere else.
/// </summary>
internal sealed class FileProtectionKeyProvider(string keyPath, string applicationIdentity) : ISecretProtectionKeyProvider
{
    private const int KeyLength = 32;
    private readonly object _gate = new();

    public string ApplicationIdentity { get; } = applicationIdentity;

    public byte[]? ReadKey()
    {
        lock (_gate)
        {
            if (!File.Exists(keyPath))
                return null;

            var key = File.ReadAllBytes(keyPath);
            if (key.Length == KeyLength)
                return key;

            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException($"The host protection key at '{keyPath}' has an invalid length.");
        }
    }

    public bool TryAddKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            if (File.Exists(keyPath))
                return false;

            using var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(key);
            return true;
        }
    }
}
