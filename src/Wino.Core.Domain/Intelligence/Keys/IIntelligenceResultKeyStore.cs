#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Kept out of Wino.Core.Domain.Interfaces on purpose: only the key lifecycle and the result
// reader should take this dependency, and a namespace nothing else imports keeps it that way.
namespace Wino.Core.Domain.Intelligence.Keys;

/// <summary>The public half of a device result key, as the rest of the app sees it.</summary>
public sealed record IntelligenceResultKey(
    string KeyId,
    Guid WinoUserId,
    string PublicKeyPem,
    DateTime CreatedUtc);

/// <summary>
/// Holds the device result keys in the intelligence database, with the private half wrapped
/// by DPAPI. The unwrapped private key never leaves this store and lives only for one decrypt.
/// </summary>
public interface IIntelligenceResultKeyStore
{
    Task<IntelligenceResultKey?> GetActiveKeyAsync(Guid winoUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IntelligenceResultKey>> GetKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the user's active key, creating one when none exists.</summary>
    Task<IntelligenceResultKey> GetOrCreateAsync(Guid winoUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens one encoded content envelope that the server encrypted to <paramref name="keyId"/>.
    /// The key's Wino user, the mailbox and the route are the authenticated data.
    /// Throws <see cref="IntelligenceResultKeyLostException"/> when this device cannot open it.
    /// </summary>
    Task<byte[]> DecryptAsync(
        string keyId,
        ReadOnlyMemory<byte> encodedEnvelope,
        Guid mailboxId,
        string route,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string keyId, CancellationToken cancellationToken = default);

    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The device can no longer open results encrypted to a key: the row is gone, DPAPI cannot
/// unwrap it (profile reset, database copied from another machine) or the envelope does not
/// match it. Retrying never helps, so the job is dropped and a new key is made.
/// </summary>
public sealed class IntelligenceResultKeyLostException(string keyId, Exception? innerException = null)
    : Exception($"The intelligence result key '{keyId}' is not usable on this device.", innerException)
{
    public string KeyId { get; } = keyId;
}
