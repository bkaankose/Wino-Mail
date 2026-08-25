using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.MenuItems;

public sealed class NewTaskListMenuItem : MenuItemBase { }

public sealed partial class TaskSmartViewMenuItem : MenuItemBase<TaskViewKind>
{
    public string Title { get; }
    public string Glyph { get; }

    /// <summary>Remaining open tasks, shown as a trailing badge in the pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    public partial int Count { get; set; }

    public bool HasCount => Count > 0;

    public TaskSmartViewMenuItem(TaskViewKind view, string title, string glyph) : base(view, null)
    {
        Title = title;
        Glyph = glyph;
    }
}

public sealed partial class AccountTaskListMenuItem : MenuItemBase<AccountTaskList>
{
    public string Title => Parameter?.Title ?? string.Empty;
    public string AccountName { get; }

    /// <summary>Remaining open tasks, shown as a trailing badge in the pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    public partial int Count { get; set; }

    public bool HasCount => Count > 0;

    public AccountTaskListMenuItem(AccountTaskList list, string accountName) : base(list, list?.Id)
        => AccountName = accountName;
}
