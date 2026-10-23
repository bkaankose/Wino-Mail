using MailCal.Core.Domain.MenuItems;

namespace MailCal.Calendar.ViewModels.Data;

/// <summary>
/// One account's calendars in the calendar navigation pane. Rendered as a navigation item
/// whose template hosts the expander and per-calendar checkboxes.
/// </summary>
public sealed partial class AccountCalendarGroupMenuItem(GroupedAccountCalendarViewModel group)
    : MenuItemBase<GroupedAccountCalendarViewModel>(group, group.Account?.Id)
{
}
