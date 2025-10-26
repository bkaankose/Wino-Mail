using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoMailItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SingleMailItemTemplate { get; set; }
    public DataTemplate? ThreadMailItemTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is MailItemViewModel)
            return SingleMailItemTemplate ?? throw new Exception($"Missing template for single mail items.");
        else if (item is ThreadMailItemViewModel)
            return ThreadMailItemTemplate ?? throw new Exception($"Missing template for thread mail items.");

        return base.SelectTemplateCore(item, container);
    }
}
