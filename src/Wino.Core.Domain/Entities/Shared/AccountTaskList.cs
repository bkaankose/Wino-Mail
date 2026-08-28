using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

[Table("TaskList")]
public class AccountTaskList
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Indexed]
    public Guid MailAccountId { get; set; }

    [Indexed]
    public TaskSourceKind SourceKind { get; set; }

    [Indexed]
    public string RemoteId { get; set; }

    public string RemoteVersion { get; set; }
    public string RemoteOrder { get; set; }
    public string ListDeltaLink { get; set; }
    public string TaskDeltaLink { get; set; }
    public string Title { get; set; }
    public string ColorHex { get; set; }
    [Indexed]
    public Guid? GroupId { get; set; }
    public int SortOrder { get; set; }
    public bool IsDefault { get; set; }
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Outlook's well-known Tasks list belongs directly to the account. Outlook does not allow
    /// deleting it or placing it in a user-created task-list group.
    /// </summary>
    [Ignore]
    public bool IsOutlookDefaultList => SourceKind == TaskSourceKind.Outlook && IsDefault;

    public string DeltaLink { get; set; }

    /// <summary>Legacy account cursors. New synchronization state is stored in TaskSyncState.</summary>
    public string SubstrateGroupDeltaLink { get; set; }
    public string SubstrateFolderDeltaLink { get; set; }

    /// <summary>
    /// Per-list view state mirrored from substrate. Graph cannot express any of it.
    /// <see cref="SortKind"/> is only set when substrate reports a sort Wino recognizes.
    /// </summary>
    public TaskSortKind? SortKind { get; set; }
    public bool SortAscending { get; set; } = true;
    public bool ShowCompletedTasks { get; set; } = true;
    public DateTime? LastSuccessfulSyncUtc { get; set; }
    public DateTime? WatermarkUtc { get; set; }
    public TaskPendingMutation PendingMutation { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;
}
