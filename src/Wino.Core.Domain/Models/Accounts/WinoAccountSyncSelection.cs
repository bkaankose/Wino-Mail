namespace Wino.Core.Domain.Models.Accounts;

public sealed record WinoAccountSyncSelection(
    bool IncludePreferences = true,
    bool IncludeAccounts = true)
{
    /// <summary>
    /// Theme and layout to export with the preferences. The app's appearance belongs to the UI layer, so the
    /// caller captures it; null exports none.
    /// </summary>
    public SyncSnapshotAppearance? Appearance { get; init; }
}
