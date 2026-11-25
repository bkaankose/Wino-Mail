using Wino.Mail.Linux.Views.Calendar;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>The calendar mode's rows (date picker, account groups, ungrouped calendars) come from one provider.</summary>
internal sealed partial class ShellSidebarView
{
    private CalendarSidebarRowProvider? _calendarRows;

    internal CalendarSidebarRowProvider? CalendarRows => _calendarRows;

    private void SetUpCalendar()
    {
        var provider = new CalendarSidebarRowProvider(_viewModel.PreferencesService, _error);
        _calendarRows = provider;
        _bindings.Own(provider);
        AddRowProvider(provider);
    }
}
