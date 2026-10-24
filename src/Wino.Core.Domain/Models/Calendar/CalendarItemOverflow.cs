using System;
using System.Collections.Generic;
using System.Globalization;

namespace Wino.Core.Domain.Models.Calendar;

public static class CalendarItemOverflow
{
    public const int MaxAllDayLanes = 3;

    public const double MoreButtonHeight = 20d;

    public const double MoreButtonGap = 4d;

    public static int FitStack(IReadOnlyList<double> itemHeights, double availableHeight, double gap, double moreButtonHeight)
    {
        ArgumentNullException.ThrowIfNull(itemHeights);

        var count = itemHeights.Count;

        if (count == 0)
        {
            return 0;
        }

        // Trying the full stack first avoids a "1 more event" link hiding an item that fits in its place.
        if (CountFitting(itemHeights, availableHeight, gap) == count)
        {
            return count;
        }

        var shown = 0;
        var used = 0d;

        while (shown < count)
        {
            var next = used + (shown > 0 ? gap : 0d) + itemHeights[shown];

            if (next + gap + moreButtonHeight > availableHeight)
            {
                break;
            }

            used = next;
            shown++;
        }

        return shown;
    }

    public static string FormatMoreText(int hiddenCount)
        => string.Format(
            CultureInfo.CurrentCulture,
            hiddenCount == 1 ? Translator.Calendar_MoreEvents_Singular : Translator.Calendar_MoreEvents_Plural,
            hiddenCount);

    public static string FormatAccessibleName(int hiddenCount, DateOnly date, CultureInfo? culture)
    {
        var displayCulture = culture ?? CultureInfo.CurrentCulture;

        return string.Format(
            displayCulture,
            Translator.Calendar_MoreEvents_AccessibleName,
            hiddenCount,
            date.ToDateTime(TimeOnly.MinValue).ToString("D", displayCulture));
    }

    private static int CountFitting(IReadOnlyList<double> itemHeights, double availableHeight, double gap)
    {
        var used = 0d;

        for (var index = 0; index < itemHeights.Count; index++)
        {
            used += (index > 0 ? gap : 0d) + itemHeights[index];

            if (used > availableHeight)
            {
                return index;
            }
        }

        return itemHeights.Count;
    }
}
