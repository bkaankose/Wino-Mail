using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.ViewModels.Data;

namespace Wino.Selectors;

public partial class AccountReorderTemplateSelector : DataTemplateSelector
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
