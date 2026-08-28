namespace Wino.Core.Domain.Enums;

/// <summary>Identifies where an account task list is stored.</summary>
public enum TaskSourceKind
{
    Local = 0,
    Gmail = 1,
    Outlook = 2
}

public enum TaskPendingMutation
{
    None = 0,
    Create = 1,
    Update = 2,
    Delete = 3
}

public enum TaskSynchronizationType
{
    Full = 0,
    Delta = 1,
    ExecuteRequests = 2,
    Strict = 3
}

public enum TaskViewKind
{
    All = 0,
    Planned = 1,
    Completed = 2,
    MyDay = 3,
    Important = 4
}

/// <summary>
/// Which completion states a task surface lists. One choice rather than additive toggles,
/// so the header control cannot reach a state the surface has to correct.
/// </summary>
public enum TaskCompletionScope
{
    Active = 0,
    Completed = 1,
    All = 2
}

/// <summary>Client-side ordering applied to a task surface.</summary>
public enum TaskSortKind
{
    DueDate = 0,
    Importance = 1,
    MyDay = 2,
    Alphabetical = 3,
    CreationDate = 4
}

public enum TaskSynchronizerOperation
{
    CreateList = 0,
    UpdateList = 1,
    DeleteList = 2,
    CreateTask = 3,
    UpdateTask = 4,
    DeleteTask = 5,
    CreateStep = 6,
    UpdateStep = 7,
    DeleteStep = 8,
    CreateGroup = 9,
    UpdateGroup = 10,
    DeleteGroup = 11,
    UpdateListPlacement = 12
}
