using System;
using System.Globalization;

namespace Wino.Core.Domain;

public static class DateTimeDisplayFormatter
{
    public static string GetTimeFormat(CultureInfo culture = null)
        => (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.ShortTimePattern;

    public static string FormatTime(DateTime dateTime, CultureInfo culture = null)
    {
        var displayCulture = culture ?? CultureInfo.CurrentCulture;

        return dateTime.ToString(GetTimeFormat(displayCulture), displayCulture);
    }

    public static string FormatTime(TimeSpan timeSpan, CultureInfo culture = null)
        => FormatTime(DateTime.Today.Add(timeSpan), culture);
}
