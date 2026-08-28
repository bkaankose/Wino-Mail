using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// The pane's first row. It carries the "new group" action too, because that action is an
/// icon button hosted inside this row rather than a navigation item of its own.
/// </summary>
public sealed class NewTaskListMenuItem : MenuItemBase
{
    public Func<Task> NewGroupRequested { get; set; }
}

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

public sealed class AccountTaskListAccountMenuItem : MenuItemBase<MailAccount, IMenuItem>, IAccountNavigationMenuItem
{
    public AccountTaskListAccountMenuItem(MailAccount account) : base(account, account?.Id)
        => IsExpanded = true;

    public string AccountName => Parameter?.Name ?? string.Empty;
    public string AccountAddress => Parameter?.Address ?? string.Empty;
    public MailAccount Account => Parameter;
    public int UnreadItemCount => 0;
    public bool IsSynchronizationProgressVisible => false;
    public bool IsProgressIndeterminate => false;
    public double SynchronizationProgressValue => 0;
    public bool IsAttentionRequired => false;
    public bool SupportsMailAccountActions => false;

    public void UpdateAccount(MailAccount account)
    {
        Parameter = account;
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountAddress));
        OnPropertyChanged(nameof(Account));
    }
}

public sealed class AccountTaskListGroupMenuItem : MenuItemBase<AccountTaskListGroup, IMenuItem>
{
    public string Title => Parameter?.Title ?? string.Empty;

    /// <summary>
    /// Groups mirrored from the provider are read-only: there is no write API for them, so a
    /// local rename or ungroup would silently drift from the server on the next sync.
    /// </summary>
    public bool IsEditable => Parameter?.SourceKind is TaskSourceKind.Local or TaskSourceKind.Outlook;

    /// <summary>
    /// Deleting is only offered once the group is empty. A group that still holds lists is
    /// emptied through "ungroup lists" first, so no delete can ever take lists with it.
    /// </summary>
    public bool IsEmpty => SubMenuItems.Count == 0;

    public bool CanDelete => IsEditable && IsEmpty;

    /// <summary>Ungrouping is only meaningful while the group still holds lists.</summary>
    public bool CanUngroup => IsEditable && !IsEmpty;

    public Func<AccountTaskListGroupMenuItem, Task> NewListRequested { get; init; }
    public Func<AccountTaskListGroupMenuItem, Task> RenameRequested { get; init; }
    public Func<AccountTaskListGroupMenuItem, Task> UngroupRequested { get; init; }
    public Func<AccountTaskListGroupMenuItem, Task> DeleteRequested { get; init; }
    public Func<IMenuItem, IMenuItem, bool, Task> DropRequested { get; init; }

    public AccountTaskListGroupMenuItem(AccountTaskListGroup group) : base(group, group?.Id)
        => IsExpanded = group?.IsExpanded ?? true;

    /// <summary>Raised by the pane after it reconciles this group's children.</summary>
    public void NotifyChildrenChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanUngroup));
    }

    public void Update(AccountTaskListGroup group)
    {
        Parameter = group;
        IsExpanded = group.IsExpanded;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanUngroup));
    }
}

public sealed partial class AccountTaskListMenuItem : MenuItemBase<AccountTaskList>
{
    private const string DefaultListColorHex = "#808080";

    public string Title => Parameter?.Title ?? string.Empty;
    public string AccountName { get; private set; }
    public string ColorHex => string.IsNullOrWhiteSpace(Parameter?.ColorHex) ? DefaultListColorHex : Parameter.ColorHex;
    public bool IsGrouped => Parameter?.GroupId is not null;
    public bool CanDelete => Parameter is not null && !Parameter.IsReadOnly && !Parameter.IsOutlookDefaultList;
    public bool CanMoveToGroup => Parameter is not null && !Parameter.IsOutlookDefaultList;
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
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanMoveToGroup));
        OnPropertyChanged(nameof(AvailableGroups));
    }
}
