using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.ViewModels.Data;

namespace Wino.Calendar.Selectors;

public partial class CustomAreaCalendarItemSelector : DataTemplateSelector
{
    public DataTemplate AllDayTemplate { get; set; }
    public DataTemplate MultiDayTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is CalendarItemViewModel calendarItemViewModel)
        {
            return calendarItemViewModel.CalendarItem.ItemType == Core.Domain.Enums.CalendarItemType.MultiDay ||
                calendarItemViewModel.CalendarItem.ItemType == Core.Domain.Enums.CalendarItemType.MultiDayAllDay ? MultiDayTemplate : AllDayTemplate;
        }

        return base.SelectTemplateCore(item, container);
    }
}
