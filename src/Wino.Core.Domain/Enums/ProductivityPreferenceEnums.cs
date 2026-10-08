namespace Wino.Core.Domain.Enums;

public enum NewItemDestinationBehavior
{
    AskEachTime = 0,
    LastUsed = 1,
    Specific = 2
}

public enum ContactNameDisplayFormat
{
    FirstNameFirst = 0,
    LastNameFirst = 1,
    ProviderDisplayName = 2
}

public enum ContactSortOrder
{
    FirstName = 0,
    LastName = 1,
    ProviderDisplayName = 2
}

public enum ToDoStartView
{
    MyDay = 0,
    Planned = 1,
    AllTasks = 2,
    SpecificList = 3
}

public enum CompletedTaskTreatment
{
    StayVisible = 0,
    MoveToBottom = 1,
    HideAfterPeriod = 2
}

public enum CompletedTaskHideDelay
{
    OneDay = 1,
    SevenDays = 7,
    ThirtyDays = 30
}
