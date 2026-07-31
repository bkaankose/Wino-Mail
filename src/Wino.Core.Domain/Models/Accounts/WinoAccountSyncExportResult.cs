namespace Wino.Core.Domain.Models.Accounts;

public sealed class WinoAccountSyncExportResult
{
    public bool IncludedPreferences { get; init; }
    public bool IncludedAccounts { get; init; }
    public int ExportedMailboxCount { get; init; }
}
