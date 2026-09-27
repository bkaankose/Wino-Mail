using System.Threading.Tasks;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// Asks the user for the secret that unlocks a sync snapshot: the Wino Account password, or a
/// sync passphrase for accounts that sign in only with a provider. <see cref="WasRejected"/> is
/// set when a previous answer did not open the snapshot.
/// </summary>
public sealed record SyncSnapshotSecretRequest(bool IsPassphrase, bool WasRejected);

/// <summary>
/// Supplies the secret for a snapshot when no cached key opens it. Returns null when the user cancels.
/// </summary>
public delegate Task<string?> SyncSnapshotSecretPrompt(SyncSnapshotSecretRequest request);
