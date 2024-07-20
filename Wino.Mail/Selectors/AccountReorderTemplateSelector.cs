
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
    public class AccountReorderTemplateSelector : DataTemplateSelector
    {
        public DataTemplate MergedAccountReorderTemplate { get; set; }
        public DataTemplate RootAccountReorderTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is MergedAccountProviderDetailViewModel)
            {
                return MergedAccountReorderTemplate;
            }

            return RootAccountReorderTemplate;
        }
    }
}
