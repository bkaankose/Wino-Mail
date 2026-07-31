namespace Wino.Core.Domain.Models.Accounts;

public sealed class WinoAccountSyncFileExportResult
{
    public string JsonContent { get; init; } = string.Empty;
    public WinoAccountSyncExportResult ExportResult { get; init; } = new();
}
