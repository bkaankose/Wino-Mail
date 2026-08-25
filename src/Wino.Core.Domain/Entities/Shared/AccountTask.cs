using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

[Table("TaskCard")]
public class AccountTask
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Indexed]
    public Guid MailAccountId { get; set; }

    [Indexed]
    public Guid TaskListId { get; set; }

    [Indexed]
    public TaskSourceKind SourceKind { get; set; }

    [Indexed]
    public string RemoteId { get; set; }

    public string RemoteVersion { get; set; }
    public string Title { get; set; }
    public string Notes { get; set; }

    /// <summary>A date-only due value. The time component is always midnight.</summary>
    [Indexed]
    public DateTime? DueDate { get; set; }

    [Indexed]
    public bool IsCompleted { get; set; }

    /// <summary>
    /// Local importance marker. Round-trips to Graph as <c>TodoTask.Importance</c> High/Normal.
    /// Google Tasks has no equivalent field, so it stays local for Gmail accounts.
    /// </summary>
    [Indexed]
    public bool IsImportant { get; set; }

    /// <summary>
    /// The day this task was pulled into My Day, or null. Date-only, like <see cref="DueDate"/>.
    /// Membership is "this value is today", so the list empties itself at midnight without a
    /// background job. A due date never writes here; it only feeds My Day suggestions.
    /// </summary>
    [Indexed]
    public DateTime? MyDayDateUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
    public string RemoteOrder { get; set; }
    public TaskPendingMutation PendingMutation { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;

    [Ignore]
    public System.Collections.Generic.List<AccountTaskStep> Steps { get; set; } = [];

    [Ignore]
    public bool IsReadOnly { get; set; }
}
