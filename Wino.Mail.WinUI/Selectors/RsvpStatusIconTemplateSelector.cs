using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Selectors;

public partial class RsvpStatusIconTemplateSelector : DataTemplateSelector
{
    public DataTemplate NotRespondedTemplate { get; set; }
    public DataTemplate ConfirmedTemplate { get; set; }
    public DataTemplate TentativeTemplate { get; set; }
    public DataTemplate CancelledTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is CalendarItemStatus status)
        {
            return status switch
            {
                CalendarItemStatus.NotResponded => NotRespondedTemplate,
                CalendarItemStatus.Accepted => ConfirmedTemplate,
                CalendarItemStatus.Tentative => TentativeTemplate,
                CalendarItemStatus.Cancelled => CancelledTemplate,
                _ => NotRespondedTemplate
            };
        }
        
        return base.SelectTemplateCore(item, container);
    }
}
