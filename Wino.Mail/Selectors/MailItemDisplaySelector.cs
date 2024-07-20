using Wino.Mail.ViewModels.Data;

#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#else
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#endif

namespace Wino.Selectors
{
    public class MailItemDisplaySelector : DataTemplateSelector
    {
        public DataTemplate SingleMailItemTemplate { get; set; }
        public DataTemplate ThreadMailItemTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is MailItemViewModel)
                return SingleMailItemTemplate;
            else if (item is ThreadMailItemViewModel)
                return ThreadMailItemTemplate;

            return base.SelectTemplateCore(item, container);
        }
    }
}
