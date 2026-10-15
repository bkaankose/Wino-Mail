#nullable enable
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Platform storage for small secret items (the Keychain on Apple), addressed by a scope and an account inside it.
/// Scopes and accounts name existing items, so they must never change for an installed app.
/// </summary>
/// <remarks>
/// The caller owns the byte arrays it passes and receives and clears them after use. A missing item reads as null and
/// deleting a missing item succeeds; every other platform failure throws.
/// </remarks>
public interface ISecureItemStore
{
    byte[]? Read(string scope, string account);

    /// <summary>Adds or replaces the item. An existing item gets the requested access too.</summary>
    void Write(string scope, string account, byte[] bytes, SecureItemAccess access = SecureItemAccess.Default);

    /// <summary>Adds the item unless one exists; returns false and keeps the existing item otherwise.</summary>
    bool TryAdd(string scope, string account, byte[] bytes);

    void Delete(string scope, string account);

    /// <summary>Deletes every item in the scope.</summary>
    void DeleteScope(string scope);
}
