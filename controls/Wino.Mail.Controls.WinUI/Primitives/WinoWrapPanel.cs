using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wino.Mail.Controls.Primitives;

/// <summary>
/// Lays children out left to right and wraps to a new line when the available width runs out.
/// Collapsed children take no slot at all, so hiding one closes the gap instead of leaving a hole.
/// </summary>
/// <remarks>
/// WinUI has no plain wrapping panel: ItemsWrapGrid and WrapGrid are virtualizing panels that only
/// work inside a ListViewBase, and VariableSizedWrapGrid reserves a cell per child regardless of
/// visibility. Both are the reason a fixed 2x2 Grid was used before.
/// </remarks>
public sealed partial class WinoWrapPanel : Panel
{
    [GeneratedDependencyProperty(DefaultValue = 0d)]
    public partial double HorizontalSpacing { get; set; }

    [GeneratedDependencyProperty(DefaultValue = 0d)]
    public partial double VerticalSpacing { get; set; }

    /// <summary>
    /// Gets or sets whether children may wrap onto further lines. When wrapping is off every child
    /// stays on one line and is offered only the width left over, so children that can trim their
    /// content shrink instead of pushing the line wider.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsWrappingEnabled { get; set; }

    partial void OnHorizontalSpacingChanged(double newValue) => InvalidateMeasure();

    partial void OnVerticalSpacingChanged(double newValue) => InvalidateMeasure();

    partial void OnIsWrappingEnabledChanged(bool newValue) => InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!IsWrappingEnabled) return MeasureSingleLine(availableSize);

        var lineWidth = 0d;
        var lineHeight = 0d;
        var totalWidth = 0d;
        var totalHeight = 0d;
        var childAvailable = new Size(availableSize.Width, double.PositiveInfinity);

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            child.Measure(childAvailable);
            var desired = child.DesiredSize;

            var spacing = lineWidth > 0 ? HorizontalSpacing : 0;
            if (lineWidth > 0 && lineWidth + spacing + desired.Width > availableSize.Width)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + VerticalSpacing;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
                continue;
            }

            lineWidth += spacing + desired.Width;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;

        return new Size(totalWidth, totalHeight);
    }

    private Size MeasureSingleLine(Size availableSize)
    {
        var lineWidth = 0d;
        var lineHeight = 0d;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            var spacing = lineWidth > 0 ? HorizontalSpacing : 0;
            var remaining = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : Math.Max(0, availableSize.Width - lineWidth - spacing);

            child.Measure(new Size(remaining, double.PositiveInfinity));
            var desired = child.DesiredSize;

            lineWidth += spacing + desired.Width;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        return new Size(lineWidth, lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!IsWrappingEnabled) return ArrangeSingleLine(finalSize);

        var x = 0d;
        var y = 0d;
        var lineHeight = 0d;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            var desired = child.DesiredSize;
            var spacing = x > 0 ? HorizontalSpacing : 0;

            if (x > 0 && x + spacing + desired.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
                spacing = 0;
            }

            child.Arrange(new Rect(x + spacing, y, desired.Width, desired.Height));
            x += spacing + desired.Width;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        return finalSize;
    }

    private Size ArrangeSingleLine(Size finalSize)
    {
        var x = 0d;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            var desired = child.DesiredSize;
            var spacing = x > 0 ? HorizontalSpacing : 0;
            var width = Math.Min(desired.Width, Math.Max(0, finalSize.Width - x - spacing));

            child.Arrange(new Rect(x + spacing, 0, width, finalSize.Height));
            x += spacing + width;
        }

        return finalSize;
    }
}
