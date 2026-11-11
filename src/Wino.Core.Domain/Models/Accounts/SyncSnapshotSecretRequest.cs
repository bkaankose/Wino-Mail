using System.Threading.Tasks;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// Asks the user for the password of a sync snapshot. <see cref="IsNewBackup"/> is set when the
/// user chooses a password for a new backup, which the prompt should have them confirm.
/// <see cref="IsPassphrase"/> is cleared only for backups from earlier builds, which are locked
/// with the Wino Account password of that time. <see cref="WasRejected"/> is set when a previous
/// answer did not open the snapshot.
/// </summary>
/// <param name="Title">Replaces the dialog's title when the password protects something other than a snapshot.</param>
/// <param name="Description">Replaces the dialog's description, likewise.</param>
/// <param name="FieldLabel">Replaces the password field's label, likewise.</param>
/// <param name="ConfirmLabel">Replaces the confirmation field's label, likewise.</param>
public sealed record SyncSnapshotSecretRequest(bool IsPassphrase, bool WasRejected, bool IsNewBackup = false, string Title = null, string Description = null, string FieldLabel = null, string ConfirmLabel = null);

/// <summary>
/// Supplies the password for a snapshot. Returns null when the user cancels.
/// </summary>
public delegate Task<string?> SyncSnapshotSecretPrompt(SyncSnapshotSecretRequest request);
