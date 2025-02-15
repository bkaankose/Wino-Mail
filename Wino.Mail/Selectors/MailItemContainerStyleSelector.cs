using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Selectors;

public partial class MailItemContainerStyleSelector : StyleSelector
{
    public Style Thread { get; set; }

    protected override Style SelectStyleCore(object item, DependencyObject container)
    {
        if (item is ThreadMailItemViewModel)
            return Thread;
        else
            return base.SelectStyleCore(item, container);
    }
}
