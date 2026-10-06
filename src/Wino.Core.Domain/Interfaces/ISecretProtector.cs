namespace Wino.Core.Domain.Interfaces;

/// <summary>Protects bytes for the current platform identity and a stable purpose-derived context.</summary>
/// <remarks>
/// The caller owns input and returned buffers and clears them after use. Implementations do not
/// mutate input buffers. Corrupt or mismatched protected data throws CryptographicException;
/// unavailable platform protection throws PlatformNotSupportedException. Neither implies a missing secret.
/// </remarks>
public interface ISecretProtector
{
    byte[] Protect(byte[] data, byte[] context);
    byte[] Unprotect(byte[] data, byte[] context);
}
