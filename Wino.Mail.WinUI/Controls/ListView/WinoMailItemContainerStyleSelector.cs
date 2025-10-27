using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoMailItemContainerStyleSelector : StyleSelector
{
    public Style? ThreadStyle { get; set; }
    public Style? MailItemStyle { get; set; }
    protected override Style SelectStyleCore(object item, DependencyObject container)
    {
        if (item is MailItemViewModel) return MailItemStyle ?? throw new Exception($"Missing style for {nameof(MailItemViewModel)}");
        if (item is ThreadMailItemViewModel)
            return ThreadStyle ?? throw new Exception($"Missing style for {nameof(ThreadMailItemViewModel)}");

        return null;
    }
}
