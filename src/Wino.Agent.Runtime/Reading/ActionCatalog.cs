using Wino.Agent.Runtime.Plan;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Reading;

/// <summary>
/// What the runtime knows about each action beyond the shared catalog (<see cref="AssistantActionCatalog"/>): the
/// item kind it acts on, whether it creates, and which argument names carry which container type. The catalog itself
/// (names, families, argument names) is the contracts' table, so the server and the runtime cannot disagree.
/// </summary>
public static class ActionCatalog
{
    public static AssistantActionInfo Info(AgentAction action) => AssistantActionCatalog.Find(action.ToString())!;

    public static string Target(AgentAction action) => Info(action).Target;

    public static bool IsFind(AgentAction action) => AssistantActionCatalog.IsFind(action.ToString());

    public static bool Creates(AgentAction action) => Target(action) == AssistantTargets.None && action is not (AgentAction.Undo or AgentAction.SyncMail or AgentAction.ChangeSetting or AgentAction.OpenView);

    public static ItemKind? ItemKindOf(AgentAction action) => Target(action) switch
    {
        AssistantTargets.Mails => ItemKind.Mail,
        AssistantTargets.Events => ItemKind.Event,
        AssistantTargets.Tasks => ItemKind.Task,
        AssistantTargets.Contacts => ItemKind.Contact,
        AssistantTargets.Folder => ItemKind.Folder,
        AssistantTargets.List => ItemKind.TaskList,
        _ => action switch
        {
            AgentAction.CreateDraft or AgentAction.CreateReplyDraft => ItemKind.Draft,
            AgentAction.CreateEvent => ItemKind.Event,
            AgentAction.CreateTask or AgentAction.CreateTaskList => ItemKind.Task,
            AgentAction.CreateContact => ItemKind.Contact,
            AgentAction.CreateFolder => ItemKind.Folder,
            _ => null,
        },
    };

    /// <summary>The container type an argument name holds for this action.</summary>
    public static ItemKind ContainerKindOf(AgentAction action, string argName) => argName switch
    {
        "calendar" => ItemKind.Calendar,
        "category" => ItemKind.Category,
        "list" or "toList" => Info(action).Family == "contact" ? ItemKind.ContactList : ItemKind.TaskList,
        _ => ItemKind.Folder,
    };

    /// <summary>Argument names of <see cref="AgentAction.UpdateTask"/> that change the task; the rest only select it.</summary>
    public static readonly IReadOnlyList<string> TaskChangeArgs = ["newTitle", "notes", "newDue", "clearDue", "setImportant", "setMyDay", "toList"];

    /// <summary>Argument names of <see cref="AgentAction.UpdateEvent"/> that change the event; the rest only select it.</summary>
    public static readonly IReadOnlyList<string> EventChangeArgs = ["newDate", "newStart", "shiftMinutes", "durationMinutes", "newTitle", "newLocation", "slot"];

    /// <summary>Actions that delete, send or answer outward: never chosen over a close candidate without a question.</summary>
    public static bool IsDestructiveOrOutgoing(AgentAction action) => action is AgentAction.Delete or AgentAction.DeleteEvent or AgentAction.DeleteTask
        or AgentAction.DeleteContact or AgentAction.DeleteFolder or AgentAction.DeleteTaskList or AgentAction.EmptyFolder or AgentAction.SendDrafts
        or AgentAction.Unsubscribe or AgentAction.MarkJunk or AgentAction.RemoveContactFromList or AgentAction.RespondToEvent;

    /// <summary>
    /// The same operation on other target kinds (pin a mail / pin a folder; move a mail to a folder / a task to a list /
    /// a contact to a list; delete a task / an event / a folder...). The reader picks one kind from words alone; the device
    /// decides which kind the named thing is. A mail action reaches the other kinds only when its own reading cannot
    /// stand (the thing is a container or an account): mails cannot be looked up by a name, so a mail reading never
    /// loses to a task or an event that merely shares the words.
    /// </summary>
    private static readonly AgentAction[][] Families =
    [
        [AgentAction.Pin, AgentAction.PinFolder],
        [AgentAction.Unpin, AgentAction.UnpinFolder],
        [AgentAction.MarkRead, AgentAction.MarkFolderRead],
        [AgentAction.Delete, AgentAction.DeleteFolder, AgentAction.DeleteTask, AgentAction.DeleteEvent, AgentAction.DeleteContact, AgentAction.DeleteTaskList],
        [AgentAction.MoveToFolder, AgentAction.UpdateTask, AgentAction.AddContactToList, AgentAction.UpdateEvent],
        [AgentAction.RenameFolder, AgentAction.RenameTaskList, AgentAction.UpdateTask, AgentAction.UpdateEvent],
        [AgentAction.SyncFolder, AgentAction.SyncMail],
        [AgentAction.RespondToEvent, AgentAction.DeleteEvent],
        [AgentAction.SearchMail, AgentAction.FindEvents, AgentAction.FindTasks, AgentAction.FindContacts],
    ];

    /// <summary>The families <paramref name="action"/> belongs to, each as the other members.</summary>
    public static IEnumerable<AgentAction[]> FamiliesOf(AgentAction action) => Families.Where(f => f.Contains(action));

    public static bool IsMailAction(AgentAction action) => Target(action) == AssistantTargets.Mails;
    public static bool IsEventAction(AgentAction action) => Target(action) == AssistantTargets.Events;
    public static bool IsTaskAction(AgentAction action) => Target(action) == AssistantTargets.Tasks;
    public static bool IsContactAction(AgentAction action) => Target(action) == AssistantTargets.Contacts;
    public static bool IsFolderAction(AgentAction action) => Target(action) == AssistantTargets.Folder;
}
