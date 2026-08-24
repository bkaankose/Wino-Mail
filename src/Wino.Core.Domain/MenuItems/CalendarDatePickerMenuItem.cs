using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// Hosts the calendar pane's date picker. Carries the calendar mode view model so the
/// template can bind the picker to the visible range without shell involvement.
/// </summary>
public sealed class CalendarDatePickerMenuItem(ICalendarShellClient client)
    : MenuItemBase<ICalendarShellClient>(client, null)
{
}
