using System;
using System.Collections.Generic;
using Windows.Globalization.DateTimeFormatting;
using Wino.Core.Domain;

namespace Wino.Mail.WinUI.Helpers;

// A packaged app formats dates in its runtime language (the app language override), not the Windows regional format,
// so a CalendarDatePicker shows 11/19/2026 for an en-GB user. A formatter created for the user's own locale returns the
// regional short date pattern, custom patterns included; the picker formats that pattern in the app language.
public static class RegionalDateFormat
{
    private const string DefaultFormat = "shortdate";

    private static readonly Lazy<string> ShortDateValue = new(CreateShortDate);

    public static string ShortDate => ShortDateValue.Value;

    private static string CreateShortDate()
    {
        try
        {
            var patterns = new DateTimeFormatter(DefaultFormat, new List<string> { DisplayCulture.Regional.Name }).Patterns;

            return patterns.Count > 0 ? patterns[0] : DefaultFormat;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Reading the regional short date format failed. Date pickers use the app language format.");
            return DefaultFormat;
        }
    }
}
