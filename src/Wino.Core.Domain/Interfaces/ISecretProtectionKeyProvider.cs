#nullable enable
namespace Wino.Core.Domain.Interfaces;

/// <summary>The installation's 32-byte secret protection key and the identity bound into every protected envelope.</summary>
public interface ISecretProtectionKeyProvider
{
    /// <summary>The application identity (the bundle identifier on Apple). It never changes for an existing installation.</summary>
    string ApplicationIdentity { get; }

    /// <summary>Returns a copy of the key, or null when none exists. The caller clears the returned buffer.</summary>
    byte[]? ReadKey();

    /// <summary>Stores the key unless one exists; returns false and keeps the existing key otherwise.</summary>
    bool TryAddKey(byte[] key);
}
