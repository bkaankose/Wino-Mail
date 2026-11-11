namespace Wino.Agent.Runtime.Plan;

/// <summary>What a step acts on. Queries are the device's own terms (folder ids, addresses, flags, ranges).</summary>
public abstract record StepTarget
{
    /// <summary>Mails matching a query. <see cref="AccountId"/> null means every account.</summary>
    public sealed record Mails(Guid? AccountId, MailQuery Query) : StepTarget;

    /// <summary>Specific mails: a selection, the previous turn's result, or a pronoun binding.</summary>
    public sealed record MailItems(IReadOnlyList<ItemRef> Items) : StepTarget;

    public sealed record Events(Guid? AccountId, EventQuery Query) : StepTarget;

    public sealed record EventItems(IReadOnlyList<ItemRef> Items) : StepTarget;

    public sealed record Tasks(Guid? AccountId, TaskQuery Query) : StepTarget;

    public sealed record TaskItems(IReadOnlyList<ItemRef> Items) : StepTarget;

    public sealed record Contacts(ContactQuery Query) : StepTarget;

    public sealed record ContactItems(IReadOnlyList<ItemRef> Items) : StepTarget;

    public sealed record Folder(Guid AccountId, Guid FolderId) : StepTarget;

    public sealed record TaskListTarget(Guid AccountId, Guid ListId) : StepTarget;

    /// <summary>The executor chains: the items, people, sender, recipients, attendees or organizer of step <see cref="N"/>'s result.</summary>
    public sealed record FromStep(int N, string Part) : StepTarget;

    /// <summary>Nothing existing: the step creates, or is a system action.</summary>
    public sealed record None : StepTarget
    {
        public static readonly None Instance = new();
    }
}

[Flags]
public enum MailStateFlags
{
    Any = 0,
    Unread = 1,
    Read = 2,
    Flagged = 4,
    HasAttachments = 8,
    Important = 16,
    Draft = 32,
}

public enum SenderKind
{
    Any,
    Person,
    Service,
    Newsletter,
}

public enum ResultOrder
{
    Newest,
    Oldest,
}

/// <summary>
/// A mail selection. <see cref="FolderIds"/> empty with <see cref="AllFolders"/> true means every folder of the
/// account(s). <see cref="SenderAddresses"/> / <see cref="SenderDomains"/> are resolved senders; <see cref="SenderWords"/>
/// the user's words for a sender nothing resolved (the executor matches them against sender names and addresses).
/// <see cref="Limit"/> 0 means every match; 1 with <see cref="Order"/> is "the latest" / "the oldest".
/// </summary>
public sealed record MailQuery(
    IReadOnlyList<Guid> FolderIds,
    bool AllFolders,
    IReadOnlyList<string> SenderAddresses,
    IReadOnlyList<string> SenderDomains,
    string? SenderWords,
    IReadOnlyList<string> RecipientAddresses,
    string? Text,
    string? ItemType,
    MailStateFlags Flags,
    SenderKind SenderKind,
    DateTime? From,
    DateTime? To,
    int Limit,
    ResultOrder Order,
    bool Negated = false)
{
    public static MailQuery Empty => new([], true, [], [], null, [], null, null, MailStateFlags.Any, SenderKind.Any, null, null, 0, ResultOrder.Newest);
}

public sealed record EventQuery(
    IReadOnlyList<Guid> CalendarIds,
    string? TitleWords,
    IReadOnlyList<string> AttendeeAddresses,
    string? AttendeeWords,
    DateTime? From,
    DateTime? To,
    int Limit,
    bool All)
{
    public static EventQuery Empty => new([], null, [], null, null, null, 0, false);
}

public sealed record TaskQuery(
    IReadOnlyList<Guid> ListIds,
    string? TitleWords,
    DateTime? DueFrom,
    DateTime? DueTo,
    bool Overdue,
    bool Important,
    bool MyDay,
    bool IncludeCompleted,
    int Limit,
    bool All)
{
    public static TaskQuery Empty => new([], null, null, null, false, false, false, false, 0, false);
}

public sealed record ContactQuery(
    string? NameWords,
    IReadOnlyList<string> Addresses,
    Guid? ListId,
    Guid? CategoryId,
    bool FavoritesOnly,
    int Limit)
{
    public static ContactQuery Empty => new(null, [], null, null, false, 0);
}
