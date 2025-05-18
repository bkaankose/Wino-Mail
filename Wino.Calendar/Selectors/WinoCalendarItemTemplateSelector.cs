using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Calendar.Selectors;

public partial class WinoCalendarItemTemplateSelector : DataTemplateSelector
{
    public CalendarDisplayType DisplayType { get; set; }

    public DataTemplate DayWeekWorkWeekTemplate { get; set; }
    public DataTemplate MonthlyTemplate { get; set; }


    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        switch (DisplayType)
        {
            case CalendarDisplayType.Day:
            case CalendarDisplayType.Week:
            case CalendarDisplayType.WorkWeek:
                return DayWeekWorkWeekTemplate;
            case CalendarDisplayType.Month:
                return MonthlyTemplate;
            case CalendarDisplayType.Year:
                break;
            default:
                break;
        }

        return base.SelectTemplateCore(item, container);
    }
}
