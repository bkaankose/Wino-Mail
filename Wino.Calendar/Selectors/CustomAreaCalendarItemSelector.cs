using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.ViewModels.Data;

namespace Wino.Calendar.Selectors
{
    public class CustomAreaCalendarItemSelector : DataTemplateSelector
    {
        public DataTemplate AllDayTemplate { get; set; }
        public DataTemplate MultiDayTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is CalendarItemViewModel calendarItemViewModel)
            {
                return calendarItemViewModel.IsMultiDayEvent ? MultiDayTemplate : AllDayTemplate;
            }

            return base.SelectTemplateCore(item, container);
        }
    }
}
