using CommunityToolkit.Diagnostics;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Markup;

namespace Wino.Controls
{
    [ContentProperty(Name = nameof(Content))]
    public class WinoExpander : Control
    {
        private const string PART_HeaderGrid = "HeaderGrid";
        private const string PART_ContentAreaWrapper = "ContentAreaWrapper";
        private const string PART_ContentArea = "ContentArea";

        private ContentControl HeaderGrid;
        private ContentControl ContentArea;
        private Grid ContentAreaWrapper;

        public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(UIElement), typeof(WinoExpander), new PropertyMetadata(null));
        public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(nameof(Content), typeof(UIElement), typeof(WinoExpander), new PropertyMetadata(null));
        public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(WinoExpander), new PropertyMetadata(false, new PropertyChangedCallback(OnIsExpandedChanged)));
        public static readonly DependencyProperty TemplateSettingsProperty = DependencyProperty.Register(nameof(TemplateSettings), typeof(WinoExpanderTemplateSettings), typeof(WinoExpander), new PropertyMetadata(new WinoExpanderTemplateSettings()));

        public UIElement Content
        {
            get { return (UIElement)GetValue(ContentProperty); }
            set { SetValue(ContentProperty, value); }
        }

        public WinoExpanderTemplateSettings TemplateSettings
        {
            get { return (WinoExpanderTemplateSettings)GetValue(TemplateSettingsProperty); }
            set { SetValue(TemplateSettingsProperty, value); }
        }

        public bool IsExpanded
        {
            get { return (bool)GetValue(IsExpandedProperty); }
            set { SetValue(IsExpandedProperty, value); }
        }

        public UIElement Header
        {
            get { return (UIElement)GetValue(HeaderProperty); }
            set { SetValue(HeaderProperty, value); }
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            HeaderGrid = GetTemplateChild(PART_HeaderGrid) as ContentControl;
            ContentAreaWrapper = GetTemplateChild(PART_ContentAreaWrapper) as Grid;
            ContentArea = GetTemplateChild(PART_ContentArea) as ContentControl;

            Guard.IsNotNull(HeaderGrid, nameof(HeaderGrid));
            Guard.IsNotNull(ContentAreaWrapper, nameof(ContentAreaWrapper));
            Guard.IsNotNull(ContentArea, nameof(ContentArea));

            var clipComposition = ElementCompositionPreview.GetElementVisual(ContentAreaWrapper);
            clipComposition.Clip = clipComposition.Compositor.CreateInsetClip();

            ContentAreaWrapper.SizeChanged += ContentSizeChanged;
            HeaderGrid.Tapped += HeaderTapped;
        }

        private void ContentSizeChanged(object sender, SizeChangedEventArgs e)
        {
            TemplateSettings.ContentHeight = e.NewSize.Height;
            TemplateSettings.NegativeContentHeight = -1 * (double)e.NewSize.Height;
        }

        private void HeaderTapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            // Tapped is delegated from executing hover action like flag or delete.
            // No need to toggle the expander.

            if (Header is MailItemDisplayInformationControl itemDisplayInformationControl &&
                itemDisplayInformationControl.IsRunningHoverAction)
            {
                itemDisplayInformationControl.IsRunningHoverAction = false;
                return;
            }

            IsExpanded = !IsExpanded;
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

    #region Settings

    public class WinoExpanderTemplateSettings : DependencyObject
    {
        public static readonly DependencyProperty HeaderHeightProperty = DependencyProperty.Register(nameof(HeaderHeight), typeof(double), typeof(WinoExpanderTemplateSettings), new PropertyMetadata(0.0));
        public static readonly DependencyProperty ContentHeightProperty = DependencyProperty.Register(nameof(ContentHeight), typeof(double), typeof(WinoExpanderTemplateSettings), new PropertyMetadata(0.0));
        public static readonly DependencyProperty NegativeContentHeightProperty = DependencyProperty.Register(nameof(NegativeContentHeight), typeof(double), typeof(WinoExpanderTemplateSettings), new PropertyMetadata(0.0));

        public double NegativeContentHeight
        {
            get { return (double)GetValue(NegativeContentHeightProperty); }
            set { SetValue(NegativeContentHeightProperty, value); }
        }

        public double HeaderHeight
        {
            get { return (double)GetValue(HeaderHeightProperty); }
            set { SetValue(HeaderHeightProperty, value); }
        }

        public double ContentHeight
        {
            get { return (double)GetValue(ContentHeightProperty); }
            set { SetValue(ContentHeightProperty, value); }
        }
    }

    #endregion
}
