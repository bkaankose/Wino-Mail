using Wino.Core.Domain.MenuItems;

namespace Wino.Calendar.ViewModels.Data;

/// <summary>
/// One checkbox row shown below the account selectors in the ungrouped calendar pane.
/// </summary>
public sealed class UngroupedCalendarMenuItem(AccountCalendarViewModel calendar)
    : MenuItemBase<AccountCalendarViewModel>(calendar, calendar.Id)
{
    private bool _isPaneCompact;

    public bool IsPaneCompact
    {
        get => _isPaneCompact;
        set => SetProperty(ref _isPaneCompact, value);
    }

    public void UpdateCalendar(AccountCalendarViewModel calendar) => Parameter = calendar;

    public void Toggle() => Parameter.IsChecked = !Parameter.IsChecked;
}
