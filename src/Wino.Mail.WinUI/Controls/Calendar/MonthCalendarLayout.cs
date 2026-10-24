using System;
using System.Collections.Generic;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls;

/// <summary>
/// The placement of one calendar item inside a month week row. Bounds are relative to the row.
/// </summary>
internal readonly record struct MonthItemLayout(CalendarItemViewModel Item, int Column, DateOnly Date, LayoutRect Bounds);

internal readonly record struct MonthRowLayout(List<MonthItemLayout> Items, List<CalendarOverflowLayout> Overflows);

internal static class MonthCalendarLayoutCalculator
{
    public const int ColumnCount = 7;
    public const int RowCount = 6;

    private const double CellPadding = 4d;
    private const double DayLabelHeight = 20d;

    // 22 px rather than the 21 px a title line needs keeps descenders inside the tile.
    private const double ItemHeight = 22d;
    private const double ItemGap = 2d;

    /// <summary>
    /// Lays out the items of one week row. Each cell stacks its items under the day label and
    /// reports the ones that do not fit as overflow.
    /// </summary>
    public static MonthRowLayout CalculateRow(IReadOnlyList<DateOnly> dates, IEnumerable<CalendarItemViewModel> items, double cellWidth, double cellHeight)
    {
        var itemLayouts = new List<MonthItemLayout>();
        var overflows = new List<CalendarOverflowLayout>();

        if (cellWidth <= 0d || cellHeight <= 0d)
        {
            return new MonthRowLayout(itemLayouts, overflows);
        }

        var stackTop = DayLabelHeight + CellPadding;
        var availableHeight = cellHeight - CellPadding - stackTop;

        for (var column = 0; column < dates.Count; column++)
        {
            var date = dates[column];
            var cellX = column * cellWidth;
            var cellItems = new List<CalendarItemViewModel>(GetCellItems(items, date));

            if (cellItems.Count == 0)
            {
                continue;
            }

            var itemHeights = new double[cellItems.Count];
            Array.Fill(itemHeights, ItemHeight);

            var shownCount = CalendarItemOverflow.FitStack(itemHeights, availableHeight, ItemGap, CalendarItemOverflow.MoreButtonHeight);
            var nextItemY = stackTop;

            for (var index = 0; index < shownCount; index++)
            {
                itemLayouts.Add(new MonthItemLayout(
                    cellItems[index],
                    column,
                    date,
                    new LayoutRect(
                        cellX + CellPadding,
                        nextItemY,
                        Math.Max(0, cellWidth - (CellPadding * 2)),
                        ItemHeight)));

                nextItemY += ItemHeight + ItemGap;
            }

            if (shownCount < cellItems.Count)
            {
                overflows.Add(new CalendarOverflowLayout(
                    date,
                    column,
                    cellItems.GetRange(shownCount, cellItems.Count - shownCount),
                    GetMoreButtonBounds(column, cellWidth, cellHeight)));
            }
        }

        return new MonthRowLayout(itemLayouts, overflows);
    }

    public static LayoutRect GetMoreButtonBounds(int column, double cellWidth, double cellHeight)
        => new(
            (column * cellWidth) + CellPadding,
            Math.Max(0d, cellHeight - CellPadding - CalendarItemOverflow.MoreButtonHeight),
            Math.Max(0d, cellWidth - (CellPadding * 2)),
            CalendarItemOverflow.MoreButtonHeight);

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
}
