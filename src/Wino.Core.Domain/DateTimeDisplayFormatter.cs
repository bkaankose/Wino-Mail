using System;
using System.Globalization;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain;

public static class DateTimeDisplayFormatter
{
    private const string TwentyFourHourTimeFormat = "HH:mm";
    private const string TwelveHourTimeFormat = "h:mm tt";

    public static DayHeaderDisplayType GetDefaultTimeDisplayType(CultureInfo culture)
        => UsesTwentyFourHourClock(culture)
            ? DayHeaderDisplayType.TwentyFourHour
            : DayHeaderDisplayType.TwelveHour;

    public static DayHeaderDisplayType GetTimeDisplayType(TimeFormatPreference timeFormatPreference, CultureInfo culture)
        => timeFormatPreference switch
        {
            TimeFormatPreference.UseLanguageCulture => GetDefaultTimeDisplayType(culture),
            TimeFormatPreference.TwelveHour => DayHeaderDisplayType.TwelveHour,
            TimeFormatPreference.TwentyFourHour => DayHeaderDisplayType.TwentyFourHour,
            _ => throw new ArgumentOutOfRangeException(nameof(timeFormatPreference))
        };

    public static bool UsesTwentyFourHourClock(CultureInfo culture)
    {
        var timePattern = (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.ShortTimePattern;

        return ContainsTimePatternSpecifier(timePattern, 'H');
    }

    /// <summary>
    /// The culture's short time pattern when it uses the requested clock (so regional separators such as "H.mm" stay),
    /// otherwise a plain pattern for that clock.
    /// </summary>
    public static string GetTimeFormat(DayHeaderDisplayType displayType, CultureInfo culture)
    {
        var pattern = (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.ShortTimePattern;
        var usesTwentyFourHours = ContainsTimePatternSpecifier(pattern, 'H');

        return displayType switch
        {
            DayHeaderDisplayType.TwentyFourHour => usesTwentyFourHours ? pattern : TwentyFourHourTimeFormat,
            DayHeaderDisplayType.TwelveHour => !usesTwentyFourHours && ContainsTimePatternSpecifier(pattern, 'h') ? pattern : TwelveHourTimeFormat,
            _ => throw new ArgumentOutOfRangeException(nameof(displayType))
        };
    }

    public static string FormatTime(DateTime dateTime, DayHeaderDisplayType displayType, CultureInfo culture)
        => dateTime.ToString(GetTimeFormat(displayType, culture), culture ?? CultureInfo.CurrentCulture);

    public static string FormatTime(TimeSpan timeSpan, DayHeaderDisplayType displayType, CultureInfo culture)
        => FormatTime(DateTime.Today.Add(timeSpan), displayType, culture);

    /// <summary>The culture's month and day ("19 November", "November 19"), optionally with an abbreviated month.</summary>
    public static string GetMonthDayPattern(CultureInfo culture, bool abbreviatedMonth = false)
    {
        var pattern = (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.MonthDayPattern;

        return abbreviatedMonth ? AbbreviateMonth(pattern) : pattern;
    }

    /// <summary>The culture's long date without the weekday ("19 November 2026", "November 19, 2026").</summary>
    public static string GetDatePattern(CultureInfo culture, bool abbreviatedMonth = false)
    {
        var pattern = RemoveWeekday((culture ?? CultureInfo.CurrentCulture).DateTimeFormat.LongDatePattern);

        return abbreviatedMonth ? AbbreviateMonth(pattern) : pattern;
    }

    /// <summary>
    /// The weekday with the month and day, placed where the culture's long date puts the weekday
    /// ("Thursday, 19 November", "19 November Thursday").
    /// </summary>
    public static string GetWeekdayMonthDayPattern(CultureInfo culture, bool abbreviatedWeekday = false, bool abbreviatedMonth = false)
    {
        var format = (culture ?? CultureInfo.CurrentCulture).DateTimeFormat;
        var monthDay = GetMonthDayPattern(culture ?? CultureInfo.CurrentCulture, abbreviatedMonth);
        var weekday = abbreviatedWeekday ? "ddd" : "dddd";
        var longDate = format.LongDatePattern.Trim();
        var index = longDate.IndexOf("dddd", StringComparison.Ordinal);

        if (index < 0)
            return $"{weekday}, {monthDay}";

        if (index + 4 == longDate.Length)
        {
            var start = index;
            while (start > 0 && IsWeekdaySeparator(longDate[start - 1])) start--;

            return monthDay + longDate[start..index] + weekday;
        }

        var end = index + 4;
        while (end < longDate.Length && IsWeekdaySeparator(longDate[end])) end++;

        return weekday + longDate[(index + 4)..end] + monthDay;
    }

    /// <summary>
    /// A month-day range that names a shared month once, on the side the culture puts the month:
    /// "16 - 22 November", "November 16 - 22", "11月16日 - 22日".
    /// </summary>
    public static string FormatMonthDayRange(DateTime start, DateTime end, CultureInfo culture, bool abbreviatedMonth = false, string separator = " - ")
    {
        culture ??= CultureInfo.CurrentCulture;
        var pattern = GetMonthDayPattern(culture, abbreviatedMonth);
        var monthStart = pattern.IndexOf('M');

        if (start.Year != end.Year || start.Month != end.Month || monthStart < 0)
            return $"{start.ToString(pattern, culture)}{separator}{end.ToString(pattern, culture)}";

        var dayIndex = pattern.IndexOf('d');

        if (dayIndex < 0)
            return $"{start.ToString(pattern, culture)}{separator}{end.ToString(pattern, culture)}";

        // The day keeps the literals that follow it ("16." in German, "22日" in Japanese); the month keeps its own.
        return dayIndex < monthStart
            ? $"{start.ToString(CustomFormat(pattern[..monthStart]), culture)}{separator}{end.ToString(pattern, culture)}"
            : $"{start.ToString(pattern, culture)}{separator}{end.ToString(CustomFormat(pattern[dayIndex..]), culture)}";
    }

    // A one-letter format such as "d" would be read as a standard format, so it gets the custom-format prefix.
    private static string CustomFormat(string part)
    {
        var trimmed = part.Trim();

        return trimmed.Length == 1 ? "%" + trimmed : trimmed;
    }

    /// <summary>The culture's long date with the weekday, optionally with abbreviated weekday and month ("Thu, 19 Nov 2026").</summary>
    public static string GetLongDatePattern(CultureInfo culture, bool abbreviated = false)
    {
        var pattern = (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.LongDatePattern;

        return abbreviated ? AbbreviateMonth(pattern).Replace("dddd", "ddd", StringComparison.Ordinal) : pattern;
    }

    private static string AbbreviateMonth(string pattern)
        => pattern.Replace("MMMM", "MMM", StringComparison.Ordinal);

    private static string RemoveWeekday(string pattern)
    {
        var index = pattern.IndexOf("dddd", StringComparison.Ordinal);
        if (index < 0)
            return pattern;

        var start = index;
        var end = index + 4;

        if (index == 0)
        {
            while (end < pattern.Length && IsWeekdaySeparator(pattern[end])) end++;
        }
        else
        {
            while (start > 0 && IsWeekdaySeparator(pattern[start - 1])) start--;
        }

        return (pattern[..start] + pattern[end..]).Trim();
    }

    // Only spacing and punctuation next to the weekday is dropped; letters such as the Japanese 日 belong to the date.
    private static bool IsWeekdaySeparator(char character)
        => character != '\'' &&(char.IsWhiteSpace(character) || char.IsPunctuation(character));

    private static bool ContainsTimePatternSpecifier(string pattern, char specifier)
    {
        var isQuoted = false;

        foreach (var character in pattern)
        {
            if (character == '\'')
            {
                isQuoted = !isQuoted;
                continue;
            }

            if (!isQuoted && character == specifier)
            {
                return true;
            }
        }

        return false;
    }
}
