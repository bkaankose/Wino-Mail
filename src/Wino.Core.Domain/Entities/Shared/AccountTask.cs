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

    /// <summary>Position under the Manual sort. Local only: no provider receives or supplies it.</summary>
    public int? ManualOrder { get; set; }

    /// <summary>When the task reminds, with date and time. The due date stays date-only; the reminder carries the time.</summary>
    [Indexed]
    public DateTime? ReminderAtUtc { get; set; }

    /// <summary>A snoozed reminder fires again at this time. Local only.</summary>
    public DateTime? ReminderSnoozedUntilUtc { get; set; }

    /// <summary>Kept in step with <see cref="IsImportant"/>, which stays the importance star; see <c>TaskFields.GetEffectivePriority</c>.</summary>
    public TaskPriority Priority { get; set; }

    /// <summary>Category names separated by line feeds.</summary>
    public string? Categories { get; set; }

    /// <summary>A date-only start date, stored so copies and exports keep it.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>An RFC 5545 RRULE value without the "RRULE:" prefix, or null.</summary>
    public string? Recurrence { get; set; }

    public TaskPendingMutation PendingMutation { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;

    [Ignore]
    public System.Collections.Generic.List<AccountTaskStep> Steps { get; set; } = [];

    [Ignore]
    public bool IsReadOnly { get; set; }
}
