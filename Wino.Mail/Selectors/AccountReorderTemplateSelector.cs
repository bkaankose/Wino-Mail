using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

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
