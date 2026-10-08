using Wino.Core.Domain.MenuItems;

namespace Wino.Calendar.ViewModels.Data;

/// <summary>
/// One account's calendars in the calendar navigation pane. Rendered as a navigation item
/// whose template hosts the expander and per-calendar checkboxes.
/// </summary>
public sealed partial class AccountCalendarGroupMenuItem(GroupedAccountCalendarViewModel group)
    : MenuItemBase<GroupedAccountCalendarViewModel>(group, group.Account?.Id)
{
}
