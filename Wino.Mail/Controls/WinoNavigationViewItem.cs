using System.Numerics;
using Microsoft.UI.Xaml.Controls;

#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
#else
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
#endif
namespace Wino.Controls
{
    public class WinoNavigationViewItem : NavigationViewItem
    {
        public bool IsDraggingItemOver
        {
            get { return (bool)GetValue(IsDraggingItemOverProperty); }
            set { SetValue(IsDraggingItemOverProperty, value); }
        }

        public static readonly DependencyProperty IsDraggingItemOverProperty = DependencyProperty.Register(nameof(IsDraggingItemOver), typeof(bool), typeof(WinoNavigationViewItem), new PropertyMetadata(false, OnIsDraggingItemOverChanged));

        private static void OnIsDraggingItemOverChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is WinoNavigationViewItem control)
                control.UpdateDragEnterState();
        }

        private void UpdateDragEnterState()
        {
            // TODO: Add animation. Maybe after overriding DragUI in shell?

            //if (IsDraggingItemOver)
            //{
            //    ScaleAnimation(new System.Numerics.Vector3(1.2f, 1.2f, 1.2f));
            //}
            //else
            //{
            //    ScaleAnimation(new System.Numerics.Vector3(1f, 1f, 1f));
            //}
        }

        private void ScaleAnimation(Vector3 vector)
        {
            if (this.Content is UIElement content)
            {
                var visual = ElementCompositionPreview.GetElementVisual(content);
                visual.Scale = vector;
            }
        }
    }
}
