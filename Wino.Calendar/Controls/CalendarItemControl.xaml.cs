using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.Controls
{
    public sealed partial class CalendarItemControl : UserControl
    {
        public static readonly DependencyProperty CalendarItemProperty = DependencyProperty.Register(nameof(CalendarItem), typeof(ICalendarItem), typeof(CalendarItemControl), new PropertyMetadata(null, new PropertyChangedCallback(OnCalendarItemChanged)));
        public static readonly DependencyProperty IsDraggingProperty = DependencyProperty.Register(nameof(IsDragging), typeof(bool), typeof(CalendarItemControl), new PropertyMetadata(false));

        public ICalendarItem CalendarItem
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
    }
}
