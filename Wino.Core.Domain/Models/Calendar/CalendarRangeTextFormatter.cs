using System;
using System.Globalization;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public sealed class CalendarRangeTextFormatter : ICalendarRangeTextFormatter
{
    public string Format(VisibleDateRange range, IDateContextProvider dateContextProvider)
    {
        var culture = dateContextProvider.Culture;

        if (range.DayCount >= 28)
        {
            return FormatMonth(range.PrimaryDate, culture);
        }

        if (range.DayCount == 1 || range.DisplayType == CalendarDisplayType.Day)
        {
            return FormatDate(range.StartDate, culture);
        }

        if (range.SpansSingleMonth)
        {
            return $"{FormatDate(range.StartDate, culture)} - {FormatDay(range.EndDate, culture)}";
        }

        return $"{FormatDate(range.StartDate, culture)} - {FormatDate(range.EndDate, culture)}";
    }

    private static string FormatDate(DateOnly date, CultureInfo culture)
        => date.ToString(culture.DateTimeFormat.MonthDayPattern, culture);

    private static string FormatDay(DateOnly date, CultureInfo culture)
        => date.Day.ToString(culture);

    private static string FormatMonth(DateOnly date, CultureInfo culture)
        => date.ToString(culture.DateTimeFormat.YearMonthPattern, culture);
}
