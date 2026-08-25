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
    public string ListDeltaLink { get; set; }
    public string TaskDeltaLink { get; set; }
    public string Title { get; set; }
    public bool IsDefault { get; set; }
    public bool IsReadOnly { get; set; }
    public string DeltaLink { get; set; }
    public DateTime? LastSuccessfulSyncUtc { get; set; }
    public DateTime? WatermarkUtc { get; set; }
    public TaskPendingMutation PendingMutation { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;
}
