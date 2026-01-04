using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoMailItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SingleMailItemTemplate { get; set; }
    public DataTemplate? ThreadMailItemTemplate { get; set; }
    public DataTemplate? CalendarMailItemTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is MailItemViewModel mailItemViewModel)
        {
            // Check if it's a calendar-related item
            if (mailItemViewModel.MailCopy.ItemType != MailItemType.Mail && CalendarMailItemTemplate != null)
                return CalendarMailItemTemplate;

            return SingleMailItemTemplate ?? throw new Exception($"Missing template for single mail items.");
        }
        else if (item is ThreadMailItemViewModel)
            return ThreadMailItemTemplate ?? throw new Exception($"Missing template for thread mail items.");

        return base.SelectTemplateCore(item, container);
    }
}
