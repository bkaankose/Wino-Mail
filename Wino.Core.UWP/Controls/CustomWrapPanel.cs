using System;

namespace Wino.Core.UWP.Controls
{
    using Windows.Foundation;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Controls;

    namespace CustomControls
    {
        public class CustomWrapPanel : Panel
        {
            protected override Size MeasureOverride(Size availableSize)
            {
                double currentRowWidth = 0;
                double currentRowHeight = 0;
                double totalHeight = 0;

                foreach (UIElement child in Children)
                {
                    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                    var childDesiredSize = child.DesiredSize;

                    if (currentRowWidth + childDesiredSize.Width > availableSize.Width)
                    {
                        // Wrap to the next row
                        totalHeight += currentRowHeight;
                        currentRowWidth = 0;
                        currentRowHeight = 0;
                    }

                    currentRowWidth += childDesiredSize.Width;
                    currentRowHeight = Math.Max(currentRowHeight, childDesiredSize.Height);
                }

                totalHeight += currentRowHeight;

                return new Size(availableSize.Width, totalHeight);
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                double currentRowWidth = 0;
                double currentRowHeight = 0;
                double currentY = 0;

                foreach (UIElement child in Children)
                {
                    var childDesiredSize = child.DesiredSize;

                    if (currentRowWidth + childDesiredSize.Width > finalSize.Width)
                    {
                        currentY += currentRowHeight;
                        currentRowWidth = 0;
                        currentRowHeight = 0;
                    }

                    child.Arrange(new Rect(new Point(currentRowWidth, currentY), childDesiredSize));

                    currentRowWidth += childDesiredSize.Width;
                    currentRowHeight = Math.Max(currentRowHeight, childDesiredSize.Height);
                }

                return finalSize;
            }
        }
    }

}
