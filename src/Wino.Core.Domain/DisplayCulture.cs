using System;
using System.Globalization;

namespace Wino.Core.Domain;

/// <summary>
/// The process cultures. Text uses the app language (CurrentUICulture); dates, times and numbers use the device's
/// regional format (CurrentCulture) with day and month names in the app language.
/// </summary>
public static class DisplayCulture
{
    // Read once, before Apply replaces CurrentCulture; later reads would only see the app's own culture.
    private static readonly Lazy<CultureInfo> RegionalValue = new(static () => CultureInfo.CurrentCulture);

    /// <summary>The device's regional format, including the user's custom patterns on Windows.</summary>
    public static CultureInfo Regional => RegionalValue.Value;

    public static void Apply(string languageCode)
    {
        var regional = Regional;
        var language = new CultureInfo(languageCode);

        CultureInfo.DefaultThreadCurrentCulture = Create(language, regional);
        CultureInfo.DefaultThreadCurrentUICulture = language;
    }

    public static CultureInfo Create(CultureInfo language, CultureInfo regional)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(regional);

        var culture = (CultureInfo)language.Clone();
        var source = regional.DateTimeFormat;
        var target = culture.DateTimeFormat;

        target.ShortDatePattern = source.ShortDatePattern;
        target.LongDatePattern = source.LongDatePattern;
        target.ShortTimePattern = source.ShortTimePattern;
        target.LongTimePattern = source.LongTimePattern;
        target.FullDateTimePattern = source.FullDateTimePattern;
        target.MonthDayPattern = source.MonthDayPattern;
        target.YearMonthPattern = source.YearMonthPattern;
        target.DateSeparator = source.DateSeparator;
        target.TimeSeparator = source.TimeSeparator;
        target.FirstDayOfWeek = source.FirstDayOfWeek;
        target.CalendarWeekRule = source.CalendarWeekRule;

        // A 12-hour regional pattern needs AM/PM text, which some languages leave empty.
        if (string.IsNullOrEmpty(target.AMDesignator) || string.IsNullOrEmpty(target.PMDesignator))
        {
            target.AMDesignator = source.AMDesignator;
            target.PMDesignator = source.PMDesignator;
        }

        var numbers = culture.NumberFormat;
        var regionalNumbers = regional.NumberFormat;

        numbers.NumberDecimalSeparator = regionalNumbers.NumberDecimalSeparator;
        numbers.NumberGroupSeparator = regionalNumbers.NumberGroupSeparator;
        numbers.NumberGroupSizes = regionalNumbers.NumberGroupSizes;
        numbers.PercentDecimalSeparator = regionalNumbers.PercentDecimalSeparator;
        numbers.PercentGroupSeparator = regionalNumbers.PercentGroupSeparator;
        numbers.CurrencyDecimalSeparator = regionalNumbers.CurrencyDecimalSeparator;
        numbers.CurrencyGroupSeparator = regionalNumbers.CurrencyGroupSeparator;

        return CultureInfo.ReadOnly(culture);
    }
}
