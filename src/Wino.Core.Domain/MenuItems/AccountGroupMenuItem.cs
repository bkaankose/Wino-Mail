using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// A user-named account group in a navigation pane. It is never selected: invoking it only expands or collapses it.
/// Its accounts are not given the group as their parent, so expanding a folder or account never opens a collapsed group.
/// </summary>
public sealed partial class AccountGroupMenuItem : MenuItemBase<MailAccountGroup, IMenuItem>
{
    public AccountGroupMenuItem(MailAccountGroup group) : base(group, group?.Id)
        => IsExpanded = group?.IsExpanded ?? true;

    public string Name => Parameter?.Name ?? string.Empty;

    public string? ColorKey => AccountGroupColors.Normalize(Parameter?.ColorKey);

    public int AccountCount => SubMenuItems.Count;

    /// <summary>The summed unread count of the group's accounts; the Mail pane fills it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnreadCountVisible))]
    public partial int UnreadItemCount { get; set; }

    /// <summary>A collapsed group shows the unread count its hidden account rows would show.</summary>
    public bool IsUnreadCountVisible => !IsExpanded && UnreadItemCount > 0;

    public string AutomationName => string.Format(
        IsExpanded ? Translator.AccountGroup_AutomationNameExpanded : Translator.AccountGroup_AutomationNameCollapsed,
        Name);

    public void Update(MailAccountGroup group)
    {
        if (group == null)
            return;

        Parameter = group;

        if (IsExpanded != group.IsExpanded)
            IsExpanded = group.IsExpanded;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ColorKey));
        OnPropertyChanged(nameof(AutomationName));
    }

    /// <summary>Sums the unread counts of the account rows inside the group.</summary>
    public void RefreshUnreadItemCount()
        => UnreadItemCount = SubMenuItems.OfType<IAccountMenuItem>().Sum(item => item.UnreadItemCount);

    public void NotifyChildrenChanged() => OnPropertyChanged(nameof(AccountCount));

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(IsExpanded))
        {
            OnPropertyChanged(nameof(IsUnreadCountVisible));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public override string ToString() => Name;
}
