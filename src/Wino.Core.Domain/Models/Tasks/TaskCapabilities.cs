using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Tasks;

/// <summary>
/// Which task fields a task source stores on its server. A field the source does not store is kept in
/// the local database only, and a server change never overwrites it. Shared code reads this instead of
/// checking the source type.
/// </summary>
public sealed record TaskCapabilities
{
    public TaskSourceKind Source { get; init; }

    /// <summary>True for lists without a server: every field lives in the local database.</summary>
    public bool IsLocal { get; init; }

    public bool SupportsReminder { get; init; }
    public bool SupportsRecurrence { get; init; }

    /// <summary>Low, normal and high, not just the importance star.</summary>
    public bool SupportsPriorityLevels { get; init; }

    /// <summary>The importance star round-trips to the server.</summary>
    public bool SupportsImportance { get; init; }

    public bool SupportsCategories { get; init; }
    public bool SupportsStartDate { get; init; }
    public bool SupportsSteps { get; init; }
    public bool SupportsServerOrder { get; init; }

    public static TaskCapabilities Local { get; } = new()
    {
        Source = TaskSourceKind.Local,
        IsLocal = true,
        SupportsReminder = true,
        SupportsRecurrence = true,
        SupportsPriorityLevels = true,
        SupportsImportance = true,
        SupportsCategories = true,
        SupportsStartDate = true,
        SupportsSteps = true
    };

    /// <summary>Microsoft Graph todoTask.</summary>
    public static TaskCapabilities Outlook { get; } = new()
    {
        Source = TaskSourceKind.Outlook,
        SupportsReminder = true,
        SupportsRecurrence = true,
        SupportsPriorityLevels = true,
        SupportsImportance = true,
        SupportsCategories = true,
        SupportsStartDate = true,
        SupportsSteps = true
    };

    /// <summary>Google Tasks stores a title, notes, a due day, completion and subtasks only.</summary>
    public static TaskCapabilities Gmail { get; } = new()
    {
        Source = TaskSourceKind.Gmail,
        SupportsSteps = true,
        SupportsServerOrder = true
    };

    /// <summary>VTODO: VALARM, RRULE, PRIORITY, CATEGORIES and DTSTART.</summary>
    public static TaskCapabilities CalDav { get; } = new()
    {
        Source = TaskSourceKind.CalDav,
        SupportsReminder = true,
        SupportsRecurrence = true,
        SupportsPriorityLevels = true,
        SupportsImportance = true,
        SupportsCategories = true,
        SupportsStartDate = true,
        SupportsSteps = true
    };

    /// <summary>EWS Task. Recurrence is not written by Wino, so Wino creates the next occurrence itself.</summary>
    public static TaskCapabilities ExchangeEws { get; } = new()
    {
        Source = TaskSourceKind.Exchange,
        SupportsReminder = true,
        SupportsPriorityLevels = true,
        SupportsImportance = true,
        SupportsCategories = true,
        SupportsStartDate = true
    };

    /// <summary>MAPI over HTTP writes the subject, body, due date, importance and completion only.</summary>
    public static TaskCapabilities ExchangeMapi { get; } = new()
    {
        Source = TaskSourceKind.Exchange,
        SupportsPriorityLevels = true,
        SupportsImportance = true
    };

    public static TaskCapabilities ForSource(TaskSourceKind source, ExchangeTransport exchangeTransport = ExchangeTransport.Ews)
        => source switch
        {
            TaskSourceKind.Outlook => Outlook,
            TaskSourceKind.Gmail => Gmail,
            TaskSourceKind.CalDav => CalDav,
            TaskSourceKind.Exchange => exchangeTransport == ExchangeTransport.Ews ? ExchangeEws : ExchangeMapi,
            _ => Local
        };
}
