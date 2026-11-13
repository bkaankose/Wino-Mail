using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// An account group that keeps its accounts' folders separate. It is never selected: invoking it only expands or
/// collapses it. Its accounts are not given the group as their parent, so expanding a folder or account never
/// opens a collapsed group.
/// </summary>
public sealed partial class AccountGroupMenuItem : MenuItemBase<MergedInbox, IMenuItem>
{
    public AccountGroupMenuItem(MergedInbox group) : base(group, group?.Id)
    {
    }

    public string Name => Parameter?.Name ?? string.Empty;

    public string? ColorKey => AccountGroupColors.Normalize(Parameter?.ColorKey);

    public WinoIconGlyph IconGlyph => AccountGroupIcons.GetGlyph(Parameter?.Icon ?? AccountGroupIcon.PeopleLink);

    public int AccountCount => SubMenuItems.Count;

    /// <summary>The summed unread count of the group's accounts; the Mail pane fills it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnreadCountVisible))]
    public partial int UnreadItemCount { get; set; }

    /// <summary>
    /// A collapsed group shows the unread count its hidden account rows would show. <see cref="MenuItemBase.IsExpanded"/>
    /// is the remembered state; the Windows row reads its own on-screen state instead, because a closed pane collapses it.
    /// </summary>
    public bool IsUnreadCountVisible => !IsExpanded && UnreadItemCount > 0;

    /// <summary>The expansion is announced by the row's expand and collapse state, which can differ from <see cref="MenuItemBase.IsExpanded"/> while the pane is closed.</summary>
    public string AutomationName => string.Format(Translator.AccountGroup_AutomationName, Name);

    public void Update(MergedInbox group)
    {
        if (group == null)
            return;

        Parameter = group;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ColorKey));
        OnPropertyChanged(nameof(IconGlyph));
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
            OnPropertyChanged(nameof(IsUnreadCountVisible));
    }

    public override string ToString() => Name;
}
