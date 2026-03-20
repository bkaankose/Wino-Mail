using System;
using System.Globalization;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public sealed class CalendarRangeTextFormatter : ICalendarRangeTextFormatter
{
    public string Format(VisibleDateRange range, IDateContextProvider dateContextProvider)
    {
        var culture = dateContextProvider.Culture;
        var startText = FormatDate(range.StartDate, culture);

        if (range.DisplayType == CalendarDisplayType.Day)
        {
            return startText;
        }

        return $"{startText} - {FormatDate(range.EndDate, culture)}";
    }

    private static string FormatDate(DateOnly date, CultureInfo culture)
        => date.ToString(culture.DateTimeFormat.ShortDatePattern, culture);
}
