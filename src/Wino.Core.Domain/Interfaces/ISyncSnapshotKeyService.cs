#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Derives sync snapshot keys from the account password or a sync passphrase and caches the
/// derived key per Wino user on this device. The secret itself is never stored.
/// </summary>
public interface ISyncSnapshotKeyService
{
    /// <summary>The parameters this device uses for a new snapshot of the user.</summary>
    SyncSnapshotKeyParameters CreateDefaultParameters(Guid? userId, byte keySource);

    /// <summary>The cached key, when this device derived one for the user before.</summary>
    Task<SyncSnapshotKey?> GetCachedKeyAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Derives the key with the given parameters. Nothing is cached.</summary>
    Task<SyncSnapshotKey> DeriveAsync(string secret, SyncSnapshotKeyParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>Caches a key that opened, or will produce, the user's snapshot.</summary>
    Task RememberAsync(Guid userId, SyncSnapshotKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Derives and caches the key from the account password at sign-in, so later exports and
    /// imports need no prompt.
    /// </summary>
    Task RememberPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default);

    Task ForgetAsync(Guid userId);
}
