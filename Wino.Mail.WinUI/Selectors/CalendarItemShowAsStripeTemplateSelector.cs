using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Enums;

namespace Wino.Selectors;

/// <summary>
/// DataTemplateSelector that selects the appropriate stripe template based on CalendarItemShowAs status.
/// </summary>
public partial class CalendarItemShowAsStripeTemplateSelector : DataTemplateSelector
{
    public DataTemplate FreeTemplate { get; set; }
    public DataTemplate TentativeTemplate { get; set; }
    public DataTemplate BusyTemplate { get; set; }
    public DataTemplate OutOfOfficeTemplate { get; set; }
    public DataTemplate WorkingElsewhereTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is CalendarItemViewModel calendarItem)
        {
            return calendarItem.CalendarItem.ShowAs switch
            {
                CalendarItemShowAs.Free => FreeTemplate,
                CalendarItemShowAs.Tentative => TentativeTemplate,
                CalendarItemShowAs.Busy => BusyTemplate,
                CalendarItemShowAs.OutOfOffice => OutOfOfficeTemplate,
                CalendarItemShowAs.WorkingElsewhere => WorkingElsewhereTemplate,
                _ => BusyTemplate // Default to Busy
            };
        }

        return base.SelectTemplateCore(item, container);
    }
}
