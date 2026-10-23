using System;
using SQLite;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Entities.Shared;

[Table("TaskStep")]
public class AccountTaskStep
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Indexed]
    public Guid TaskId { get; set; }

    [Indexed]
    public Guid MailAccountId { get; set; }

    public TaskSourceKind SourceKind { get; set; }
    public string RemoteId { get; set; }
    public string RemoteVersion { get; set; }
    public string Title { get; set; }
    public bool IsCompleted { get; set; }
    public int Order { get; set; }
    public TaskPendingMutation PendingMutation { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;

    [Ignore]
    public bool IsReadOnly { get; set; }
}
