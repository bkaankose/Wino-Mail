using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

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

        /// <summary>
        /// The background is set to this brush when an item is dragged over it
        /// </summary>
        public Brush OnDragOverBackground
        {
            get { return (Brush)GetValue(OnDragOverBackgroundProperty); }
            set { SetValue(OnDragOverBackgroundProperty, value); }
        }

        // Using a DependencyProperty as the backing store for OnDragOverBackground.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty OnDragOverBackgroundProperty =
            DependencyProperty.Register("OnDragOverBackground", typeof(Brush), typeof(WinoNavigationViewItem), new PropertyMetadata(null));

        private Brush _defaultBackground;

        private static void OnIsDraggingItemOverChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is WinoNavigationViewItem control)
                control.UpdateDragEnterState();
        }

        private void UpdateDragEnterState()
        {
            // TODO: Add animation. Maybe after overriding DragUI in shell?

            if (IsDraggingItemOver)
            {
                _defaultBackground = Background;
                Background = OnDragOverBackground;
            }
            else
            {
                Background = _defaultBackground;
            }
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
