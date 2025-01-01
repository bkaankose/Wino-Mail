using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Messages;

namespace Wino.Calendar.Controls
{
    public sealed partial class CalendarItemControl : UserControl
    {
        public static readonly DependencyProperty CalendarItemProperty = DependencyProperty.Register(nameof(CalendarItem), typeof(CalendarItemViewModel), typeof(CalendarItemControl), new PropertyMetadata(null, new PropertyChangedCallback(OnCalendarItemChanged)));
        public static readonly DependencyProperty IsDraggingProperty = DependencyProperty.Register(nameof(IsDragging), typeof(bool), typeof(CalendarItemControl), new PropertyMetadata(false));

        public CalendarItemViewModel CalendarItem
        {
            get { return (CalendarItemViewModel)GetValue(CalendarItemProperty); }
            set { SetValue(CalendarItemProperty, value); }
        }

        public bool IsDragging
        {
            get { return (bool)GetValue(IsDraggingProperty); }
            set { SetValue(IsDraggingProperty, value); }
        }

        public CalendarItemControl()
        {
            InitializeComponent();
        }

        private static void OnCalendarItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CalendarItemControl control)
            {
                control.UpdateVisualStates();
            }
        }

        private void UpdateVisualStates()
        {
            if (CalendarItem == null) return;

            if (CalendarItem.IsAllDayEvent)
            {
                VisualStateManager.GoToState(this, "AllDayEvent", true);
            }
            else if (CalendarItem.IsMultiDayEvent)
            {
                VisualStateManager.GoToState(this, "MultiDayEvent", true);
            }
            else
            {
                VisualStateManager.GoToState(this, "RegularEvent", true);
            }
        }

        private void ControlDragStarting(UIElement sender, DragStartingEventArgs args) => IsDragging = true;

        private void ControlDropped(UIElement sender, DropCompletedEventArgs args) => IsDragging = false;

        private void ControlTapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (CalendarItem == null) return;

            WeakReferenceMessenger.Default.Send(new CalendarItemTappedMessage(CalendarItem));
        }

        private void ControlDoubleTapped(object sender, Windows.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            if (CalendarItem == null) return;

            WeakReferenceMessenger.Default.Send(new CalendarItemDoubleTappedMessage(CalendarItem));
        }

        private void ControlRightTapped(object sender, Windows.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (CalendarItem == null) return;

            WeakReferenceMessenger.Default.Send(new CalendarItemRightTappedMessage(CalendarItem));
        }

        private void ContextFlyoutOpened(object sender, object e)
        {
            if (CalendarItem == null) return;

            if (!CalendarItem.IsSelected)
            {
                WeakReferenceMessenger.Default.Send(new CalendarItemTappedMessage(CalendarItem));
            }
        }
    }
}
