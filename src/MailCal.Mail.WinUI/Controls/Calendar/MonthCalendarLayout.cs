using System;
using System.Collections.Generic;
using MailCal.Calendar.ViewModels.Data;

namespace MailCal.Calendar.Controls;

/// <summary>
/// The placement of one calendar item inside a month week row. Bounds are relative to the row.
/// </summary>
internal readonly record struct MonthItemLayout(CalendarItemViewModel Item, int Column, DateOnly Date, LayoutRect Bounds);

internal static class MonthCalendarLayoutCalculator
{
    public const int ColumnCount = 7;
    public const int RowCount = 6;

    private const double CellPadding = 4d;
    private const double DayLabelHeight = 20d;
    private const double RegularItemHeight = 18d;
    private const double ExpandedItemHeight = 30d;
    private const double ItemGap = 2d;

    /// <summary>
    /// Lays out the items of one week row. Each cell stacks its items under the day label and
    /// drops the ones that do not fit.
    /// </summary>
    public static List<MonthItemLayout> CalculateRow(IReadOnlyList<DateOnly> dates, IEnumerable<CalendarItemViewModel> items, double cellWidth, double cellHeight)
    {
        var itemLayouts = new List<MonthItemLayout>();

        if (cellWidth <= 0d || cellHeight <= 0d)
        {
            return itemLayouts;
        }

        for (var column = 0; column < dates.Count; column++)
        {
            var date = dates[column];
            var cellX = column * cellWidth;
            var nextItemY = DayLabelHeight + CellPadding;

            foreach (var item in GetCellItems(items, date))
            {
                var itemHeight = GetItemHeight(item);

                if (nextItemY + itemHeight > cellHeight - CellPadding)
                {
                    break;
                }

                itemLayouts.Add(new MonthItemLayout(
                    item,
                    column,
                    date,
                    new LayoutRect(
                        cellX + CellPadding,
                        nextItemY,
                        Math.Max(0, cellWidth - (CellPadding * 2)),
                        itemHeight)));

                nextItemY += itemHeight + ItemGap;
            }
        }

        return itemLayouts;
    }

    private static IEnumerable<CalendarItemViewModel> GetCellItems(IEnumerable<CalendarItemViewModel> items, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);

        foreach (var item in items)
        {
            if (!CalendarItemAccessor.TryGetTimeRange(item, out var start, out var end))
            {
                continue;
            }

            var localStart = start.LocalDateTime;
            var localEnd = end.LocalDateTime;

            if (localEnd <= localStart)
            {
                continue;
            }

            if (localStart < dayEnd && localEnd > dayStart)
            {
                yield return item;
            }
        }
    }

    private static double GetItemHeight(CalendarItemViewModel item)
        => item.IsAllDayEvent || item.IsMultiDayEvent
            ? ExpandedItemHeight
            : RegularItemHeight;
}
