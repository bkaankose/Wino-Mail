using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Selectors;

public partial class AccountReorderTemplateSelector : DataTemplateSelector
{
    public DataTemplate? AccountGroupReorderTemplate { get; set; }
    public DataTemplate? MergedAccountReorderTemplate { get; set; }
    public DataTemplate? RootAccountReorderTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is AccountReorderItem { IsGroup: true })
            return AccountGroupReorderTemplate ?? throw new ArgumentException(nameof(AccountGroupReorderTemplate));

        if (item is AccountReorderItem { IsLinkedAccount: true })
            return MergedAccountReorderTemplate ?? throw new ArgumentException(nameof(MergedAccountReorderTemplate));

        return RootAccountReorderTemplate ?? throw new ArgumentException(nameof(RootAccountReorderTemplate));
    }
}
