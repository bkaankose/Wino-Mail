using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wino.Mail.WinUI.Controls.CustomControls;

/// <summary>
/// Lays children out in equal-width columns that fill the available width.
/// Each row is as tall as its tallest child, so wrapped text can grow a row without
/// forcing a uniform height on every item.
/// </summary>
public partial class UniformColumnPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(UniformColumnPanel), new PropertyMetadata(200d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(UniformColumnPanel), new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(UniformColumnPanel), new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((UniformColumnPanel)d).InvalidateMeasure();

    private (int Columns, double ColumnWidth) GetColumns(double availableWidth)
    {
        if (double.IsInfinity(availableWidth))
            return (Math.Max(1, Children.Count), MinColumnWidth);

        // Columns are not capped by the item count, so a few items keep their tile width instead of stretching.
        var columns = Math.Max(1, (int)((availableWidth + ColumnSpacing) / (MinColumnWidth + ColumnSpacing)));
        var columnWidth = Math.Max(0, (availableWidth - (columns - 1) * ColumnSpacing) / columns);
        return (columns, columnWidth);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, columnWidth) = GetColumns(availableSize.Width);

        double totalHeight = 0;
        double rowHeight = 0;
        int visibleIndex = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));

            if (child.Visibility == Visibility.Collapsed)
                continue;

            if (visibleIndex > 0 && visibleIndex % columns == 0)
            {
                totalHeight += rowHeight + RowSpacing;
                rowHeight = 0;
            }

            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            visibleIndex++;
        }

        totalHeight += rowHeight;

        var width = double.IsInfinity(availableSize.Width)
            ? columns * columnWidth + (columns - 1) * ColumnSpacing
            : availableSize.Width;

        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, columnWidth) = GetColumns(finalSize.Width);

        // Row heights are needed before arranging, because each row is as tall as its tallest child.
        var visibleCount = 0;
        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Collapsed)
                visibleCount++;
        }

        var rowHeights = new double[(visibleCount + columns - 1) / columns];
        var index = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            var row = index / columns;
            rowHeights[row] = Math.Max(rowHeights[row], child.DesiredSize.Height);
            index++;
        }

        index = 0;
        double y = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            var row = index / columns;
            var column = index % columns;

            if (column == 0 && row > 0)
                y += rowHeights[row - 1] + RowSpacing;

            child.Arrange(new Rect(column * (columnWidth + ColumnSpacing), y, columnWidth, rowHeights[row]));
            index++;
        }

        return finalSize;
    }
}
