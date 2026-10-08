using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Mail.ViewModels.Data;

public sealed record DestinationBehaviorOption(NewItemDestinationBehavior Behavior, string DisplayText);
public sealed record ContactNameDisplayOption(ContactNameDisplayFormat Format, string DisplayText);
public sealed record ContactSortOption(ContactSortOrder Order, string DisplayText);
public sealed record ToDoStartViewOption(ToDoStartView View, string DisplayText);
public sealed record CompletedTaskTreatmentOption(CompletedTaskTreatment Treatment, string DisplayText);
public sealed record CompletedTaskHideDelayOption(CompletedTaskHideDelay Delay, string DisplayText);
public sealed record ContactDestinationPreferenceOption(ContactCreateDestination Destination)
{
    public string DisplayText => Destination.DisplayName;
}

public sealed record TaskListPreferenceOption(AccountTaskList List, string AccountName)
{
    public string DisplayText => $"{AccountName} · {List.Title}";
}
