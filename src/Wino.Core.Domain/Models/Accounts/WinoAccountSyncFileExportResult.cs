using System;
using Wino.Mail.Api.Contracts.Users;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// An export destined for a file. <see cref="Content"/> is the encrypted snapshot, byte for byte
/// what the Wino Account service stores.
/// </summary>
public sealed class WinoAccountSyncFileExportResult
{
    private const string FileNamePrefix = "wino-backup-";

    public byte[] Content { get; init; } = [];
    public string FileName { get; init; } = string.Empty;
    public WinoAccountSyncExportResult ExportResult { get; init; } = new();

    /// <summary>
    /// The suggested name for a local backup file. Callers ask for the save location with this name
    /// before exporting, then write to exactly the path the platform returns. A sandboxed save panel
    /// grants access to that path only, not to sibling names in the same folder.
    /// </summary>
    public static string CreateFileName(DateTime timestamp)
        => $"{FileNamePrefix}{timestamp:yyyyMMdd-HHmm}{SyncSnapshotFormat.FileExtension}";
}
