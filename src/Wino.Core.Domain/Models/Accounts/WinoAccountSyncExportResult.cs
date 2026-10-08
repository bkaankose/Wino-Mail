namespace Wino.Core.Domain.Models.Accounts;

public sealed class WinoAccountSyncExportResult
{
    public bool IncludedPreferences { get; init; }
    public bool IncludedAccounts { get; init; }
    public int ExportedMailboxCount { get; init; }

    /// <summary>
    /// Number of exported mailboxes that carried account settings, signatures or a folder layout.
    /// </summary>
    public int ExportedAccountDataCount { get; init; }

    /// <summary>
    /// Number of app data items (templates, rules, shortcuts, categories, aliases, merged inboxes)
    /// carried by the snapshot.
    /// </summary>
    public int ExportedAppDataCount { get; init; }
}
