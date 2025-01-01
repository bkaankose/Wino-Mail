using System.Windows.Input;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Core.UWP.Controls
{
    public sealed partial class WinoAppTitleBar : UserControl
    {
        public event TypedEventHandler<WinoAppTitleBar, RoutedEventArgs> BackButtonClicked;

        public static readonly DependencyProperty IsRenderingPaneVisibleProperty = DependencyProperty.Register(nameof(IsRenderingPaneVisible), typeof(bool), typeof(WinoAppTitleBar), new PropertyMetadata(false, OnDrawingPropertyChanged));
        public static readonly DependencyProperty IsReaderNarrowedProperty = DependencyProperty.Register(nameof(IsReaderNarrowed), typeof(bool), typeof(WinoAppTitleBar), new PropertyMetadata(false, OnIsReaderNarrowedChanged));
        public static readonly DependencyProperty IsBackButtonVisibleProperty = DependencyProperty.Register(nameof(IsBackButtonVisible), typeof(bool), typeof(WinoAppTitleBar), new PropertyMetadata(false, OnDrawingPropertyChanged));
        public static readonly DependencyProperty OpenPaneLengthProperty = DependencyProperty.Register(nameof(OpenPaneLength), typeof(double), typeof(WinoAppTitleBar), new PropertyMetadata(0d, OnDrawingPropertyChanged));
        public static readonly DependencyProperty IsNavigationPaneOpenProperty = DependencyProperty.Register(nameof(IsNavigationPaneOpen), typeof(bool), typeof(WinoAppTitleBar), new PropertyMetadata(false, OnDrawingPropertyChanged));
        public static readonly DependencyProperty NavigationViewDisplayModeProperty = DependencyProperty.Register(nameof(NavigationViewDisplayMode), typeof(Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode), typeof(WinoAppTitleBar), new PropertyMetadata(Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Compact, OnDrawingPropertyChanged));
        public static readonly DependencyProperty ShellFrameContentProperty = DependencyProperty.Register(nameof(ShellFrameContent), typeof(UIElement), typeof(WinoAppTitleBar), new PropertyMetadata(null, OnDrawingPropertyChanged));
        public static readonly DependencyProperty SystemReservedProperty = DependencyProperty.Register(nameof(SystemReserved), typeof(double), typeof(WinoAppTitleBar), new PropertyMetadata(0, OnDrawingPropertyChanged));
        public static readonly DependencyProperty CoreWindowTextProperty = DependencyProperty.Register(nameof(CoreWindowText), typeof(string), typeof(WinoAppTitleBar), new PropertyMetadata(string.Empty, OnDrawingPropertyChanged));
        public static readonly DependencyProperty ReadingPaneLengthProperty = DependencyProperty.Register(nameof(ReadingPaneLength), typeof(double), typeof(WinoAppTitleBar), new PropertyMetadata(420d, OnDrawingPropertyChanged));
        public static readonly DependencyProperty ConnectionStatusProperty = DependencyProperty.Register(nameof(ConnectionStatus), typeof(WinoServerConnectionStatus), typeof(WinoAppTitleBar), new PropertyMetadata(WinoServerConnectionStatus.None, new PropertyChangedCallback(OnConnectionStatusChanged)));
        public static readonly DependencyProperty ReconnectCommandProperty = DependencyProperty.Register(nameof(ReconnectCommand), typeof(ICommand), typeof(WinoAppTitleBar), new PropertyMetadata(null));
        public static readonly DependencyProperty ShrinkShellContentOnExpansionProperty = DependencyProperty.Register(nameof(ShrinkShellContentOnExpansion), typeof(bool), typeof(WinoAppTitleBar), new PropertyMetadata(true));
        public static readonly DependencyProperty IsDragAreaProperty = DependencyProperty.Register(nameof(IsDragArea), typeof(bool), typeof(WinoAppTitleBar), new PropertyMetadata(false, new PropertyChangedCallback(OnIsDragAreaChanged)));

        public ICommand ReconnectCommand
        {
            get { return (ICommand)GetValue(ReconnectCommandProperty); }
            set { SetValue(ReconnectCommandProperty, value); }
        }

        public WinoServerConnectionStatus ConnectionStatus
        {
            get { return (WinoServerConnectionStatus)GetValue(ConnectionStatusProperty); }
            set { SetValue(ConnectionStatusProperty, value); }
        }

        public string CoreWindowText
        {
            get { return (string)GetValue(CoreWindowTextProperty); }
            set { SetValue(CoreWindowTextProperty, value); }
        }

        public bool IsDragArea
        {
            get { return (bool)GetValue(IsDragAreaProperty); }
            set { SetValue(IsDragAreaProperty, value); }
        }


        public double SystemReserved
        {
            get { return (double)GetValue(SystemReservedProperty); }
            set { SetValue(SystemReservedProperty, value); }
        }

        public UIElement ShellFrameContent
        {
            get { return (UIElement)GetValue(ShellFrameContentProperty); }
            set { SetValue(ShellFrameContentProperty, value); }
        }

        public Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode NavigationViewDisplayMode
        {
            get { return (Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode)GetValue(NavigationViewDisplayModeProperty); }
            set { SetValue(NavigationViewDisplayModeProperty, value); }
        }

        public bool ShrinkShellContentOnExpansion
        {
            get { return (bool)GetValue(ShrinkShellContentOnExpansionProperty); }
            set { SetValue(ShrinkShellContentOnExpansionProperty, value); }
        }

        public bool IsNavigationPaneOpen
        {
            get { return (bool)GetValue(IsNavigationPaneOpenProperty); }
            set { SetValue(IsNavigationPaneOpenProperty, value); }
        }

        public double OpenPaneLength
        {
            get { return (double)GetValue(OpenPaneLengthProperty); }
            set { SetValue(OpenPaneLengthProperty, value); }
        }

        public bool IsBackButtonVisible
        {
            get { return (bool)GetValue(IsBackButtonVisibleProperty); }
            set { SetValue(IsBackButtonVisibleProperty, value); }
        }

        public bool IsReaderNarrowed
        {
            get { return (bool)GetValue(IsReaderNarrowedProperty); }
            set { SetValue(IsReaderNarrowedProperty, value); }
        }

        public bool IsRenderingPaneVisible
        {
            get { return (bool)GetValue(IsRenderingPaneVisibleProperty); }
            set { SetValue(IsRenderingPaneVisibleProperty, value); }
        }

        public double ReadingPaneLength
        {
            get { return (double)GetValue(ReadingPaneLengthProperty); }
            set { SetValue(ReadingPaneLengthProperty, value); }
        }

        private static void OnIsReaderNarrowedChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is WinoAppTitleBar bar)
            {
                bar.DrawTitleBar();
            }
        }

        private static void OnDrawingPropertyChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is WinoAppTitleBar bar)
            {
                bar.DrawTitleBar();
            }
        }

        private static void OnConnectionStatusChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is WinoAppTitleBar bar)
            {
                bar.UpdateConnectionStatus();
            }
        }

        private static void OnIsDragAreaChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is WinoAppTitleBar bar)
            {
                bar.SetDragArea();
            }
        }

        private void SetDragArea()
        {
            if (IsDragArea)
            {
                Window.Current.SetTitleBar(dragbar);
            }
        }

        private void UpdateConnectionStatus()
        {

        }

        private void DrawTitleBar()
        {
            UpdateLayout();

            CoreWindowTitleTextBlock.Visibility = Visibility.Collapsed;
            ShellContentContainer.Width = double.NaN;
            ShellContentContainer.Margin = new Thickness(0, 0, 0, 0);
            ShellContentContainer.HorizontalAlignment = HorizontalAlignment.Stretch;

            EmptySpaceWidth.Width = new GridLength(1, GridUnitType.Star);

            // Menu is not visible.
            if (NavigationViewDisplayMode == Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Minimal)
            {

            }
            else if (NavigationViewDisplayMode == Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Compact)
            {
                // Icons are visible.

                if (!IsReaderNarrowed && ShrinkShellContentOnExpansion)
                {
                    ShellContentContainer.HorizontalAlignment = HorizontalAlignment.Left;
                    ShellContentContainer.Width = ReadingPaneLength;
                }
            }
            else if (NavigationViewDisplayMode == Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Expanded)
            {
                if (IsNavigationPaneOpen)
                {
                    CoreWindowTitleTextBlock.Visibility = Visibility.Visible;

                    // LMargin = OpenPaneLength - LeftMenuStackPanel
                    ShellContentContainer.Margin = new Thickness(OpenPaneLength - LeftMenuStackPanel.ActualSize.X, 0, 0, 0);

                    if (!IsReaderNarrowed && ShrinkShellContentOnExpansion)
                    {
                        ShellContentContainer.HorizontalAlignment = HorizontalAlignment.Left;
                        ShellContentContainer.Width = ReadingPaneLength;
                    }
                }
                else
                {
                    // EmptySpaceWidth.Width = new GridLength(ReadingPaneLength, GridUnitType.Pixel);
                    EmptySpaceWidth.Width = new GridLength(ReadingPaneLength, GridUnitType.Star);
                }
            }
        }

        public WinoAppTitleBar()
        {
            InitializeComponent();
        }

        private void BackClicked(object sender, RoutedEventArgs e)
        {
            BackButtonClicked?.Invoke(this, e);
        }

        private void PaneClicked(object sender, RoutedEventArgs e)
        {
            IsNavigationPaneOpen = !IsNavigationPaneOpen;
        }

        private void TitlebarSizeChanged(object sender, SizeChangedEventArgs e) => DrawTitleBar();

        private void ReconnectClicked(object sender, RoutedEventArgs e)
        {
            // Close the popup for reconnect button.
            ReconnectFlyout.Hide();

            // Execute the reconnect command.
            ReconnectCommand?.Execute(null);
        }
    }
}
