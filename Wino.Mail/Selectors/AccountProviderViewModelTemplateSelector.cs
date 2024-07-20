
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
    public class AccountProviderViewModelTemplateSelector : DataTemplateSelector
    {
        public DataTemplate RootAccountTemplate { get; set; }
        public DataTemplate MergedAccountTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is MergedAccountProviderDetailViewModel)
                return MergedAccountTemplate;
            else
                return RootAccountTemplate;
        }
    }
}
