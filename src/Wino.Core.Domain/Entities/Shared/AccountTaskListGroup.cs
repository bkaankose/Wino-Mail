using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// Organization for task lists. Outlook groups round-trip through the To Do substrate API;
/// local and Gmail groups remain application-local.
/// </summary>
[Table("TaskListGroup")]
public sealed class AccountTaskListGroup
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Indexed]
    public Guid MailAccountId { get; set; }

    /// <summary>Substrate folder group id. Null for groups created locally.</summary>
    [Indexed]
    public string RemoteId { get; set; }
    public string RemoteVersion { get; set; }
    public string RemoteOrder { get; set; }

    /// <summary>Where the group came from.</summary>
    [Indexed]
    public TaskSourceKind SourceKind { get; set; } = TaskSourceKind.Local;

    public string Title { get; set; }
    public int SortOrder { get; set; }
    public bool IsExpanded { get; set; } = true;
    public TaskPendingMutation PendingMutation { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;
}
