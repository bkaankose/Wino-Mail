using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

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
