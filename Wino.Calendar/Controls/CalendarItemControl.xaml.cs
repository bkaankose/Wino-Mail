using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.Controls
{
    public sealed partial class CalendarItemControl : UserControl
    {
        public ICalendarItem CalendarItem
        {
            get { return (CalendarItemViewModel)GetValue(CalendarItemProperty); }
            set { SetValue(CalendarItemProperty, value); }
        }

        public static readonly DependencyProperty CalendarItemProperty = DependencyProperty.Register(nameof(CalendarItem), typeof(ICalendarItem), typeof(CalendarItemControl), new PropertyMetadata(null, new PropertyChangedCallback(OnCalendarItemChanged)));

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
    }
}
