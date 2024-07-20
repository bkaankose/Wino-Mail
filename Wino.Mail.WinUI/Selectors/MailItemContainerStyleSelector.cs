
using Wino.Mail.ViewModels.Data;

#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#else
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
#endif

namespace Wino.Selectors
{
    public class MailItemContainerStyleSelector : StyleSelector
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
}
