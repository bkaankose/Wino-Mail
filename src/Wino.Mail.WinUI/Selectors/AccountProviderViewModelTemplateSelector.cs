using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.WinUI.Selectors;
using Wino.Mail.ViewModels.Common.Data;

namespace Wino.Mail.WinUI.Selectors;

public partial class AccountProviderViewModelTemplateSelector : DataTemplateSelector, IReleasableTemplateSelector
{
    private bool _isReleased;

    public DataTemplate? RootAccountTemplate { get; set; }
    public DataTemplate? MergedAccountTemplate { get; set; }

    public void ReleaseTemplates()
    {
        _isReleased = true;
        RootAccountTemplate = null;
        MergedAccountTemplate = null;
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (_isReleased)
            return base.SelectTemplateCore(item, container);

        if (item is MergedAccountProviderDetailViewModel)
            return MergedAccountTemplate ?? throw new ArgumentException(nameof(MergedAccountTemplate));
        else
            return RootAccountTemplate ?? throw new ArgumentException(nameof(RootAccountTemplate));
    }
}
