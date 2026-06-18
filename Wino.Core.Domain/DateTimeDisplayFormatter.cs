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

    public static string GetTimeFormat(DayHeaderDisplayType displayType)
        => displayType switch
        {
            DayHeaderDisplayType.TwentyFourHour => TwentyFourHourTimeFormat,
            DayHeaderDisplayType.TwelveHour => TwelveHourTimeFormat,
            _ => throw new ArgumentOutOfRangeException(nameof(displayType))
        };

    public static string FormatTime(DateTime dateTime, DayHeaderDisplayType displayType, CultureInfo culture)
        => dateTime.ToString(GetTimeFormat(displayType), culture ?? CultureInfo.CurrentCulture);

    public static string FormatTime(TimeSpan timeSpan, DayHeaderDisplayType displayType, CultureInfo culture)
        => FormatTime(DateTime.Today.Add(timeSpan), displayType, culture);

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
