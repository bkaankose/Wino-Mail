using System;
using Wino.Calendar.ViewModels.Data;

namespace Wino.Calendar.Controls;

internal static class CalendarItemAccessor
{
    public static bool TryGetTimeRange(CalendarItemViewModel item, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = new DateTimeOffset(item.StartDate);
        end = new DateTimeOffset(item.EndDate);
        return end > start;
    }
}
