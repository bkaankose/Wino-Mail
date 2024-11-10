using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.Controls
{
    public class CalendarItemControl : Control
    {
        public ICalendarItem Item
        {
            get { return (ICalendarItem)GetValue(ItemProperty); }
            set { SetValue(ItemProperty, value); }
        }

        public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(ICalendarItem), typeof(CalendarItemControl), new PropertyMetadata(null, new PropertyChangedCallback(OnItemChanged)));

        public CalendarItemControl()
        {
            DefaultStyleKey = typeof(CalendarItemControl);
        }

        private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CalendarItemControl control)
            {
                control.UpdateDateRendering();
            }
        }

        private void UpdateDateRendering()
        {
            if (Item == null) return;

            UpdateLayout();
        }

        public override string ToString()
        {
            return Item?.Name ?? "NA";
        }
    }
}
