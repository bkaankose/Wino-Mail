namespace Wino.Agent.Runtime.Plan;

/// <summary>
/// A plan the app can run as it is: every step names its account, container, items, addresses and times by the
/// device's own ids. The runtime never executes; <see cref="Ports.IAgentPlanExecutor"/> does, including step chains.
/// <see cref="MayHaveMisunderstood"/> is set when the reading's confidence was under 0.5: preview with a warning, never auto-run.
/// </summary>
public sealed record ResolvedPlan(
    Guid Id,
    IReadOnlyList<ResolvedStep> Steps,
    string? Gloss,
    double Confidence,
    bool MayHaveMisunderstood)
{
    public bool NeedsConfirmation => Steps.Any(s => s.NeedsConfirmation);
}

/// <summary>
/// One step of a plan. <see cref="DependsOn"/> and <see cref="LinkPart"/> are set when the step acts on an earlier
/// step's result (the target is then <see cref="StepTarget.FromStep"/>). <see cref="EstimatedCount"/> is the number
/// of items the target matched when the runtime could count them. <see cref="Permanent"/> marks a change that
/// cannot be undone (a delete from the deleted items, emptying a folder).
/// </summary>
public sealed record ResolvedStep(
    int N,
    AgentAction Action,
    StepTarget Target,
    StepArgs Args,
    int? DependsOn,
    string? LinkPart,
    RiskLevel Risk,
    bool NeedsConfirmation,
    string? Gloss,
    int? EstimatedCount,
    bool Permanent = false);

public enum RiskLevel
{
    None,
    Low,
    Medium,
    High,
}

/// <summary>The steps of a turn as far as they are resolved, shown next to a clarification.</summary>
public sealed record ResolvedPlanDraft(IReadOnlyList<DraftStep> Steps);

public sealed record DraftStep(int N, AgentAction Action, string? Gloss, bool Resolved);

/// <summary>A light reference to one item on the device: never content.</summary>
public sealed record ItemRef(ItemKind Kind, Guid? AccountId, string Id)
{
    /// <summary>A stable key for pickers and brain picks: <c>kind:account:id</c>.</summary>
    public string Key => $"{Kind}:{AccountId?.ToString() ?? ""}:{Id}";

    public static ItemRef? Parse(string? key)
    {
        if (key is null) return null;
        var parts = key.Split(':', 3);
        if (parts.Length != 3 || !Enum.TryParse<ItemKind>(parts[0], out var kind)) return null;
        Guid? account = parts[1].Length == 0 ? null : Guid.TryParse(parts[1], out var g) ? g : null;
        return new ItemRef(kind, account, parts[2]);
    }

    public static ItemRef Contact(string address) => new(ItemKind.Contact, null, address);
    public static ItemRef Account(Guid id) => new(ItemKind.Account, id, id.ToString());
    public static ItemRef Folder(Guid accountId, Guid id) => new(ItemKind.Folder, accountId, id.ToString());
    public static ItemRef Calendar(Guid accountId, Guid id) => new(ItemKind.Calendar, accountId, id.ToString());
    public static ItemRef TaskList(Guid accountId, Guid id) => new(ItemKind.TaskList, accountId, id.ToString());
    public static ItemRef Category(Guid accountId, Guid id) => new(ItemKind.Category, accountId, id.ToString());
    public static ItemRef ContactList(Guid id) => new(ItemKind.ContactList, null, id.ToString());
    public static ItemRef Sender(string addressOrDomain) => new(ItemKind.Sender, null, addressOrDomain);
}

public enum ItemKind
{
    Mail,
    Event,
    Task,
    Contact,
    Folder,
    Draft,
    Account,
    Calendar,
    TaskList,
    Category,
    ContactList,
    /// <summary>A sender address or domain (an org).</summary>
    Sender,
    /// <summary>An action name, for <see cref="Clarify.ClarificationKind.ChooseAction"/>.</summary>
    Action,
    /// <summary>A wall-clock time (ISO 8601), for <see cref="Clarify.ClarificationKind.ConfirmTime"/>.</summary>
    Time,
    /// <summary>A brain entry id, for <see cref="Clarify.ClarificationKind.ConfirmForget"/>.</summary>
    BrainEntry,
}

public sealed record StepOutcome(int N, StepStatus Status, IReadOnlyList<ItemRef> Touched, string? Detail = null);

public enum StepStatus
{
    Done,
    NothingMatched,
    Failed,
    Skipped,
    Cancelled,
}

public sealed record PlanOutcome(Guid PlanId, IReadOnlyList<StepOutcome> Steps);

/// <summary>The step numbers the user accepted on the preview (all of them on "Go").</summary>
public sealed record PlanConfirmation(Guid PlanId, IReadOnlyList<int> AcceptedSteps);

/// <summary>The action catalog of the contracts (<c>AssistantActions</c>) as an enum; names are identical.</summary>
public enum AgentAction
{
    SearchMail, MarkRead, MarkUnread, Flag, Unflag, Archive, Unarchive, Delete, MoveToFolder, MarkJunk, MarkNotJunk,
    MoveToFocused, MoveToOther, Pin, Unpin, Categorize, Uncategorize, Summarize, Translate, AnalyzeMail, CopyCode,
    Unsubscribe, SendDrafts, CreateDraft, CreateReplyDraft, CreateEvent, FindEvents, DeleteEvent, UpdateEvent,
    SetEventShowAs, RespondToEvent, SetEventReminder, JoinMeeting, CreateTask, FindTasks, CompleteTask, ReopenTask,
    UpdateTask, AddTaskStep, DeleteTask, CreateTaskList, RenameTaskList, DeleteTaskList, FindContacts, FavoriteContact,
    UnfavoriteContact, AddContactToList, RemoveContactFromList, CategorizeContact, CreateContact, DeleteContact,
    MarkFolderRead, EmptyFolder, PinFolder, UnpinFolder, RenameFolder, CreateFolder, DeleteFolder, SyncFolder, Undo, SyncMail,
    CreateMailRule, ChangeSetting, OpenView,
}

/// <summary>The app settings the assistant may switch (<c>AssistantSettings</c> of the contracts); each is one boolean of the app's preferences.</summary>
public enum AgentSetting
{
    NewMailNotifications,
    QuietHours,
    VoiceAutoConfirm,
    CalendarReminders,
    TaskReminders,
}

/// <summary>The app views the assistant may open (<c>AssistantViews</c> of the contracts).</summary>
public enum AgentView
{
    Mail,
    Calendar,
    Tasks,
    Contacts,
    Settings,
    MailRules,
}
