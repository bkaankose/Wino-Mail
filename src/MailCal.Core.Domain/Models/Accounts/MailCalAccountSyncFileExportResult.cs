using System;
using MailCal.Core.Domain.Models.LocalBackup;

namespace MailCal.Core.Domain.Models.Accounts;

/// <summary>
/// An export destined for a file. <see cref="Content"/> is the encrypted snapshot, byte for byte
/// what the online account service stores.
/// </summary>
public sealed class MailCalAccountSyncFileExportResult
{
    private const string FileNamePrefix = "mailcal-backup-";

    public byte[] Content { get; init; } = [];
    public string FileName { get; init; } = string.Empty;
    public MailCalAccountSyncExportResult ExportResult { get; init; } = new();

    /// <summary>
    /// The suggested name for a local backup file. Callers ask for the save location with this name
    /// before exporting, then write to exactly the path the platform returns. A sandboxed save panel
    /// grants access to that path only, not to sibling names in the same folder.
    /// </summary>
    public static string CreateFileName(DateTime timestamp)
        => $"{FileNamePrefix}{timestamp:yyyyMMdd-HHmm}{SyncSnapshotFormat.FileExtension}";
}
