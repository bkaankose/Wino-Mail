using Wino.Core.Domain.MenuItems;

namespace Wino.Calendar.ViewModels.Data;

/// <summary>
/// One checkbox row shown below the account selectors in the ungrouped calendar pane.
/// </summary>
public sealed class UngroupedCalendarMenuItem(AccountCalendarViewModel calendar)
    : MenuItemBase<AccountCalendarViewModel>(calendar, calendar.Id)
{
    public void UpdateCalendar(AccountCalendarViewModel calendar) => Parameter = calendar;

    public void Toggle() => Parameter.IsChecked = !Parameter.IsChecked;
}
