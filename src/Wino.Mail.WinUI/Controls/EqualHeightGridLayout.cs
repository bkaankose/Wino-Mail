using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// Equal-width columns where every item in a row takes the row's tallest height.
/// UniformGridLayout sizes all items to the first one, which clips cards with longer text.
/// </summary>
public sealed partial class EqualHeightGridLayout : NonVirtualizingLayout
{
    public double MinItemWidth { get; set; } = 240;
    public double ColumnSpacing { get; set; } = 8;
    public double RowSpacing { get; set; } = 8;

    private int GetColumnCount(double availableWidth, int itemCount)
    {
        if (itemCount == 0) return 1;
        if (double.IsInfinity(availableWidth)) return itemCount;

        var columns = (int)Math.Floor((availableWidth + ColumnSpacing) / (MinItemWidth + ColumnSpacing));
        return Math.Clamp(columns, 1, itemCount);
    }

    private double GetColumnWidth(double availableWidth, int columns)
        => double.IsInfinity(availableWidth)
            ? MinItemWidth
            : Math.Max(0, (availableWidth - ColumnSpacing * (columns - 1)) / columns);

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var children = context.Children;
        var columns = GetColumnCount(availableSize.Width, children.Count);
        var columnWidth = GetColumnWidth(availableSize.Width, columns);
        var height = 0d;

        for (var rowStart = 0; rowStart < children.Count; rowStart += columns)
        {
            var rowHeight = 0d;

            for (var i = rowStart; i < Math.Min(rowStart + columns, children.Count); i++)
            {
                children[i].Measure(new Size(columnWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
            }

            height += rowHeight + (rowStart > 0 ? RowSpacing : 0);
        }

        var width = double.IsInfinity(availableSize.Width)
            ? columns * columnWidth + ColumnSpacing * (columns - 1)
            : availableSize.Width;

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var children = context.Children;
        var columns = GetColumnCount(finalSize.Width, children.Count);
        var columnWidth = GetColumnWidth(finalSize.Width, columns);
        var y = 0d;

        for (var rowStart = 0; rowStart < children.Count; rowStart += columns)
        {
            var rowEnd = Math.Min(rowStart + columns, children.Count);
            var rowHeight = 0d;

            for (var i = rowStart; i < rowEnd; i++)
                rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);

            for (var i = rowStart; i < rowEnd; i++)
            {
                var x = (i - rowStart) * (columnWidth + ColumnSpacing);
                children[i].Arrange(new Rect(x, y, columnWidth, rowHeight));
            }

            y += rowHeight + RowSpacing;
        }

        return finalSize;
    }
}
