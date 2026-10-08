#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Moves the app's preferences, accounts and user-authored data between installs as one encrypted
/// snapshot. The snapshot is encrypted on this device; the Wino Account service only stores it.
/// </summary>
public interface IWinoAccountDataSyncService
{
    /// <summary>Encrypts the selected data and uploads it to the signed-in Wino Account.</summary>
    Task<WinoAccountSyncExportResult> ExportAsync(WinoAccountSyncSelection selection, SyncSnapshotSecretPrompt? secretPrompt = null, CancellationToken cancellationToken = default);

    /// <summary>Encrypts the selected data for a file. Works without a signed-in account when a passphrase is supplied.</summary>
    Task<WinoAccountSyncFileExportResult> ExportToFileAsync(WinoAccountSyncSelection selection, SyncSnapshotSecretPrompt? secretPrompt = null, CancellationToken cancellationToken = default);

    /// <summary>Downloads and applies the snapshot stored for the signed-in Wino Account.</summary>
    Task<WinoAccountSyncImportResult> ImportAsync(WinoAccountSyncSelection selection, SyncSnapshotSecretPrompt? secretPrompt = null, CancellationToken cancellationToken = default);

    /// <summary>Applies an encrypted snapshot file. Plain JSON exports are rejected.</summary>
    Task<WinoAccountSyncImportResult> ImportFromFileAsync(byte[] content, SyncSnapshotSecretPrompt? secretPrompt = null, CancellationToken cancellationToken = default);

    /// <summary>Applies theme and layout from an import. Must run on the UI thread.</summary>
    void ApplyAppearance(SyncSnapshotAppearance appearance);
}
