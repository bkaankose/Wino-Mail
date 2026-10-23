using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.ViewModels.Data;

namespace MailCal.Selectors;

public partial class AccountReorderTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MergedAccountReorderTemplate { get; set; }
    public DataTemplate? RootAccountReorderTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is MergedAccountProviderDetailViewModel)
        {
            return MergedAccountReorderTemplate ?? throw new ArgumentException(nameof(MergedAccountReorderTemplate));
        }

        return RootAccountReorderTemplate ?? throw new ArgumentException(nameof(RootAccountReorderTemplate));
    }
}
