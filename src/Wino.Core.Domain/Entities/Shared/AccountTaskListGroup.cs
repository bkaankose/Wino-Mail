using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>Application-local organization for task lists. Providers do not synchronize this entity.</summary>
[Table("TaskListGroup")]
public sealed class AccountTaskListGroup
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Indexed]
    public Guid MailAccountId { get; set; }

    public string Title { get; set; }
    public int SortOrder { get; set; }
    public bool IsExpanded { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;
}
