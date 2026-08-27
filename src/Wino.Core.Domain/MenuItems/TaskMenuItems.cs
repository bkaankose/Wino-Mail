using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

public sealed class NewTaskListMenuItem : MenuItemBase { }
public sealed class NewTaskListGroupMenuItem : MenuItemBase { }

public abstract partial class TaskSmartViewMenuItem : MenuItemBase<TaskViewKind>
{
    /// <summary>Remaining open tasks, shown as a trailing badge in the pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    public partial int Count { get; set; }

    public bool HasCount => Count > 0;

    protected TaskSmartViewMenuItem(TaskViewKind view) : base(view, null) { }
}

public sealed partial class MyDayTaskMenuItem() : TaskSmartViewMenuItem(TaskViewKind.MyDay);

public sealed partial class PlannedTaskMenuItem() : TaskSmartViewMenuItem(TaskViewKind.Planned);

public sealed partial class ImportantTaskMenuItem() : TaskSmartViewMenuItem(TaskViewKind.Important);

public sealed class AccountTaskListAccountMenuItem : MenuItemBase<MailAccount>
{
    public AccountTaskListAccountMenuItem(MailAccount account) : base(account, account?.Id) { }

    public string AccountName => Parameter?.Name ?? string.Empty;

    public void UpdateAccount(MailAccount account)
    {
        Parameter = account;
        OnPropertyChanged(nameof(AccountName));
    }
}

public sealed class AccountTaskListGroupMenuItem : MenuItemBase<AccountTaskListGroup, IMenuItem>
{
    public string Title => Parameter?.Title ?? string.Empty;
    public Func<AccountTaskListGroupMenuItem, Task> NewListRequested { get; init; }
    public Func<AccountTaskListGroupMenuItem, Task> RenameRequested { get; init; }
    public Func<AccountTaskListGroupMenuItem, Task> UngroupRequested { get; init; }
    public Func<IMenuItem, IMenuItem, bool, Task> DropRequested { get; init; }

    public AccountTaskListGroupMenuItem(AccountTaskListGroup group) : base(group, group?.Id)
        => IsExpanded = group?.IsExpanded ?? true;

    public void Update(AccountTaskListGroup group)
    {
        Parameter = group;
        IsExpanded = group.IsExpanded;
        OnPropertyChanged(nameof(Title));
    }
}

public sealed partial class AccountTaskListMenuItem : MenuItemBase<AccountTaskList>
{
    private const string DefaultListColorHex = "#808080";

    public string Title => Parameter?.Title ?? string.Empty;
    public string AccountName { get; private set; }
    public string ColorHex => string.IsNullOrWhiteSpace(Parameter?.ColorHex) ? DefaultListColorHex : Parameter.ColorHex;
    public bool IsGrouped => Parameter?.GroupId is not null;
    public IReadOnlyList<AccountTaskListGroup> AvailableGroups { get; private set; } = [];
    public Func<AccountTaskListMenuItem, Task> RenameRequested { get; init; }
    public Func<AccountTaskListMenuItem, Task> RemoveFromGroupRequested { get; init; }
    public Func<AccountTaskListMenuItem, Guid, Task> MoveToGroupRequested { get; init; }
    public Func<AccountTaskListMenuItem, Task> DeleteRequested { get; init; }
    public Func<IMenuItem, IMenuItem, bool, Task> DropRequested { get; init; }

    /// <summary>Remaining open tasks, shown as a trailing badge in the pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    public partial int Count { get; set; }

    public bool HasCount => Count > 0;

    public AccountTaskListMenuItem(AccountTaskList list, string accountName) : base(list, list?.Id)
        => AccountName = accountName;

    public void Update(AccountTaskList list, string accountName, IReadOnlyList<AccountTaskListGroup> availableGroups = null)
    {
        Parameter = list;
        AccountName = accountName;
        AvailableGroups = availableGroups ?? [];
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(ColorHex));
        OnPropertyChanged(nameof(IsGrouped));
        OnPropertyChanged(nameof(AvailableGroups));
    }
}
