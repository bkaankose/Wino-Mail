#nullable enable
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Accounts;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Derives sync snapshot keys from the backup password the user types. Neither the password nor
/// the derived key is stored, so every export and restore asks for it.
/// </summary>
public interface ISyncSnapshotKeyService
{
    /// <summary>The parameters for a new snapshot, with a fresh random salt.</summary>
    SyncSnapshotKeyParameters CreateParameters();

    /// <summary>Derives the key with the given parameters. Nothing is cached.</summary>
    Task<SyncSnapshotKey> DeriveAsync(string secret, SyncSnapshotKeyParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the keys that earlier builds cached from the online account password.
    /// </summary>
    void DeleteLegacyKeyCache();
}
