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

    public bool HasAnyRemoteData => HadRemotePreferences || RemoteMailboxCount > 0;
}
