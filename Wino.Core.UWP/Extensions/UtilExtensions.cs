using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace Wino.Extensions
{
    public static class UtilExtensions
    {
        public static float ToFloat(this double value) => (float)value;

        public static List<FrameworkElement> Children(this DependencyObject parent)
        {
            var list = new List<FrameworkElement>();

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is FrameworkElement)
                {
                    list.Add(child as FrameworkElement);
                }

                list.AddRange(Children(child));
            }

            return list;
        }

        public static T GetChildByName<T>(this DependencyObject parent, string name)
        {
            var childControls = Children(parent);
            var controls = childControls.OfType<FrameworkElement>();

            if (controls == null)
            {
                return default(T);
            }

            var control = controls
                .Where(x => x.Name.Equals(name))
                .Cast<T>()
                .First();

            return control;
        }

        public static Visual Visual(this UIElement element) =>
            ElementCompositionPreview.GetElementVisual(element);

        public static void SetChildVisual(this UIElement element, Visual childVisual) =>
            ElementCompositionPreview.SetElementChildVisual(element, childVisual);

        public static Point RelativePosition(this UIElement element, UIElement other) =>
            element.TransformToVisual(other).TransformPoint(new Point(0, 0));

        public static bool IsFullyVisibile(this FrameworkElement element, FrameworkElement parent)
        {
            if (element == null || parent == null)
                return false;

            if (element.Visibility != Visibility.Visible)
                return false;

            var elementBounds = element.TransformToVisual(parent).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var containerBounds = new Windows.Foundation.Rect(0, 0, parent.ActualWidth, parent.ActualHeight);

            var originalElementWidth = elementBounds.Width;
            var originalElementHeight = elementBounds.Height;

            elementBounds.Intersect(containerBounds);

            var newElementWidth = elementBounds.Width;
            var newElementHeight = elementBounds.Height;

            return originalElementWidth.Equals(newElementWidth) && originalElementHeight.Equals(newElementHeight);
        }

        public static void ScrollToElement(this ScrollViewer scrollViewer, FrameworkElement element,
            bool isVerticalScrolling = true, bool smoothScrolling = true, float? zoomFactor = null, bool bringToTopOrLeft = true, bool addMargin = true)
        {
            if (!bringToTopOrLeft && element.IsFullyVisibile(scrollViewer))
                return;

            var contentArea = (FrameworkElement)scrollViewer.Content;
            var position = element.RelativePosition(contentArea);

            if (isVerticalScrolling)
            {
                // Accomodate for additional header.
                scrollViewer.ChangeView(null, Math.Max(0, position.Y - (addMargin ? 48 : 0)), zoomFactor, !smoothScrolling);
            }
            else
            {
                scrollViewer.ChangeView(position.X, null, zoomFactor, !smoothScrolling);
            }
        }


        public static IEnumerable<T> GetValues<T>() => Enum.GetValues(typeof(T)).Cast<T>();
    }
}
