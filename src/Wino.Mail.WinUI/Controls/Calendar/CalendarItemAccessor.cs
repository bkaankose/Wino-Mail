using System;
using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.WinUI.Controls.Calendar;

internal static class CalendarItemAccessor
{
    public static bool TryGetTimeRange(CalendarItemViewModel item, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = new DateTimeOffset(item.StartDate);
        end = new DateTimeOffset(item.EndDate);
        return end > start;
    }
}
