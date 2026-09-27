namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// An export destined for a file. <see cref="Content"/> is the encrypted snapshot, byte for byte
/// what the Wino Account service stores.
/// </summary>
public sealed class WinoAccountSyncFileExportResult
{
    public byte[] Content { get; init; } = [];
    public string FileName { get; init; } = string.Empty;
    public WinoAccountSyncExportResult ExportResult { get; init; } = new();
}
