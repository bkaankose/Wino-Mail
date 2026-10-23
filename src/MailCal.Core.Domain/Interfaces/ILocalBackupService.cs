#nullable enable
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Accounts;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Moves the app's preferences, accounts and user-authored data between installs as one encrypted
/// backup file. The file is encrypted on this device with a password the user types.
/// </summary>
public interface ILocalBackupService
{
    /// <summary>Encrypts the selected data for a file.</summary>
    Task<MailCalAccountSyncFileExportResult> ExportToFileAsync(MailCalAccountSyncSelection selection, SyncSnapshotSecretPrompt? secretPrompt = null, CancellationToken cancellationToken = default);

    /// <summary>Applies an encrypted backup file. Plain JSON exports are rejected.</summary>
    Task<MailCalAccountSyncImportResult> ImportFromFileAsync(byte[] content, SyncSnapshotSecretPrompt? secretPrompt = null, CancellationToken cancellationToken = default);

    /// <summary>Applies theme and layout from an import. Must run on the UI thread.</summary>
    void ApplyAppearance(SyncSnapshotAppearance appearance);
}
