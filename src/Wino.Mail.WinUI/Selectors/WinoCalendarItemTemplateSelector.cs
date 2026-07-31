using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Selectors;

public partial class WinoCalendarItemTemplateSelector : DataTemplateSelector
{
    public CalendarDisplayType DisplayType { get; set; }

    public DataTemplate DayWeekWorkWeekTemplate { get; set; } = null!;
    public DataTemplate MonthlyTemplate { get; set; } = null!;


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
            default:
                break;
        }

        return base.SelectTemplateCore(item, container) ?? DayWeekWorkWeekTemplate;
    }
}
