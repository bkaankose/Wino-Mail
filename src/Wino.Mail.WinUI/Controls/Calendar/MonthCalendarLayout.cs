using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls;

public sealed class MonthCellLayout
{
    public MonthCellLayout(DateOnly date, int index, int row, int column, LayoutRect bounds)
    {
        Date = date;
        Index = index;
        Row = row;
        Column = column;
        Bounds = bounds;
    }

    public DateOnly Date { get; set; }
    public int Index { get; set; }
    public int Row { get; set; }
    public int Column { get; set; }
    public LayoutRect Bounds { get; set; }
}

public sealed class MonthItemLayout
{
    public MonthItemLayout(CalendarItemViewModel item, int cellIndex, DateOnly date, LayoutRect bounds, DataTemplate? template = null)
    {
        Item = item;
        CellIndex = cellIndex;
        Date = date;
        Bounds = bounds;
        Template = template;
    }

    public CalendarItemViewModel Item { get; set; }
    public int CellIndex { get; set; }
    public DateOnly Date { get; set; }
    public LayoutRect Bounds { get; set; }
    public DataTemplate? Template { get; set; }
}

public sealed class MonthCellLabelLayout
{
    public MonthCellLabelLayout(string dayText, double labelOpacity, LayoutRect bounds)
    {
        DayText = dayText;
        LabelOpacity = labelOpacity;
        Bounds = bounds;
    }

    public string DayText { get; set; }
    public double LabelOpacity { get; set; }
    public LayoutRect Bounds { get; set; }
}

internal sealed record MonthCalendarLayoutResult(double CellWidth, double CellHeight, IReadOnlyList<MonthCellLayout> Cells, IReadOnlyList<MonthItemLayout> Items);

internal static class MonthCalendarLayoutCalculator
{
    public const int ColumnCount = 7;
    public const int RowCount = 6;

    private const double CellPadding = 4d;
    private const double DayLabelHeight = 20d;
    private const double RegularItemHeight = 18d;
    private const double ExpandedItemHeight = 30d;
    private const double ItemGap = 2d;

    public static MonthCalendarLayoutResult Calculate(VisibleDateRange range, IEnumerable<CalendarItemViewModel> items, double availableWidth, double availableHeight)
    {
        var cellWidth = availableWidth <= 0 ? 0d : availableWidth / ColumnCount;
        var cellHeight = availableHeight <= 0 ? 0d : availableHeight / RowCount;
        var cells = range.Dates
            .Select((date, index) =>
            {
                var row = index / ColumnCount;
                var column = index % ColumnCount;
                return new MonthCellLayout(
                    date,
                    index,
                    row,
                    column,
                    new LayoutRect(column * cellWidth, row * cellHeight, cellWidth, cellHeight));
            })
            .ToArray();

        var itemLayouts = new List<MonthItemLayout>();

        foreach (var cell in cells)
        {
            var cellItems = GetCellItems(items, cell.Date).ToList();
            var nextItemY = cell.Bounds.Y + DayLabelHeight + CellPadding;

            for (var index = 0; index < cellItems.Count; index++)
            {
                var itemHeight = GetItemHeight(cellItems[index]);

                if (nextItemY + itemHeight > cell.Bounds.Y + cell.Bounds.Height - CellPadding)
                {
                    break;
                }

                itemLayouts.Add(new MonthItemLayout(
                    cellItems[index],
                    cell.Index,
                    cell.Date,
                    new LayoutRect(
                        cell.Bounds.X + CellPadding,
                        nextItemY,
                        Math.Max(0, cell.Bounds.Width - (CellPadding * 2)),
                        itemHeight)));

                nextItemY += itemHeight + ItemGap;
            }
        }

        return new MonthCalendarLayoutResult(cellWidth, cellHeight, cells, itemLayouts);
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
