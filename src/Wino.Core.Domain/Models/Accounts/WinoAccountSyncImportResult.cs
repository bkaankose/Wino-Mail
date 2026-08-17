namespace Wino.Core.Domain.Models.Accounts;

public sealed class WinoAccountSyncImportResult
{
    public bool IncludedPreferences { get; init; }
    public bool IncludedAccounts { get; init; }
    public bool HadRemotePreferences { get; init; }
    public int AppliedPreferenceCount { get; init; }
    public int FailedPreferenceCount { get; init; }
    public int ImportedMailboxCount { get; init; }
    public int SkippedDuplicateMailboxCount { get; init; }
    public int RemoteMailboxCount { get; init; }

    /// <summary>
    /// Number of accounts whose settings, signatures or folder layout were updated from the payload.
    /// </summary>
    public int AppliedAccountDataCount { get; init; }

    /// <summary>
    /// Number of folders whose navigation layout was applied directly. Folders that do not exist yet
    /// are parked and applied when the synchronizer creates them, so they are not counted here.
    /// </summary>
    public int AppliedFolderConfigurationCount { get; init; }

    public bool HasAnyRemoteData => HadRemotePreferences || RemoteMailboxCount > 0 || AppliedAccountDataCount > 0;
}
