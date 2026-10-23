namespace MailCal.Core.Domain.Enums;

public enum DraftSyncState
{
    None = 0,
    PendingSync = 1,
    SyncFailed = 2,
    Synced = 3
}
