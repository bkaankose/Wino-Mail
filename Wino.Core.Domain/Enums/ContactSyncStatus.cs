namespace Wino.Core.Domain.Enums;

/// <summary>
/// Represents the synchronization status of a contact.
/// </summary>
public enum ContactSyncStatus
{
    /// <summary>
    /// Contact is in sync with its source.
    /// </summary>
    Synced = 0,

    /// <summary>
    /// Contact has local changes that need to be pushed to the server.
    /// </summary>
    PendingUpload = 1,

    /// <summary>
    /// Contact has remote changes that need to be pulled from the server.
    /// </summary>
    PendingDownload = 2,

    /// <summary>
    /// Contact has both local and remote changes (conflict).
    /// </summary>
    Conflict = 3,

    /// <summary>
    /// Contact is deleted locally and deletion needs to be synced.
    /// </summary>
    PendingDeletion = 4,

    /// <summary>
    /// Contact failed to synchronize.
    /// </summary>
    SyncFailed = 5
}
