using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;

namespace Wino.Controls
{
    [ContentProperty(Name = nameof(Content))]
    public class WinoExpander : Control
    {
        public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(UIElement), typeof(WinoExpander), new PropertyMetadata(null));
        public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(nameof(Content), typeof(UIElement), typeof(WinoExpander), new PropertyMetadata(null));
        public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(WinoExpander), new PropertyMetadata(false, new PropertyChangedCallback(OnIsExpandedChanged)));

        public bool IsExpanded
        {
            get { return (bool)GetValue(IsExpandedProperty); }
            set { SetValue(IsExpandedProperty, value); }
        }

        public UIElement Content
        {
            get { return (UIElement)GetValue(ContentProperty); }
            set { SetValue(ContentProperty, value); }
        }

        public UIElement Header
        {
            get { return (UIElement)GetValue(HeaderProperty); }
            set { SetValue(HeaderProperty, value); }
        }

        private static void OnIsExpandedChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is WinoExpander control)
                control.UpdateVisualStates();
        }

        private void UpdateVisualStates()
        {
            VisualStateManager.GoToState(this, IsExpanded ? "Expanded" : "Collapsed", true);
        }
    }
}
