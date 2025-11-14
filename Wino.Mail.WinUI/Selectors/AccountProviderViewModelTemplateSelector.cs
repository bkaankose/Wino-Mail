using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.ViewModels.Data;

namespace Wino.Selectors;

public partial class AccountProviderViewModelTemplateSelector : DataTemplateSelector
{
    public DataTemplate? RootAccountTemplate { get; set; }
    public DataTemplate? MergedAccountTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is MergedAccountProviderDetailViewModel)
            return MergedAccountTemplate ?? throw new ArgumentException(nameof(MergedAccountTemplate));
        else
            return RootAccountTemplate ?? throw new ArgumentException(nameof(RootAccountTemplate));
    }
}
