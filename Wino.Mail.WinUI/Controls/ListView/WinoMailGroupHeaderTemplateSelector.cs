using System;
using CommunityToolkit.Mvvm.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoMailGroupHeaderTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DefaultHeaderTemplate { get; set; }
    public DataTemplate? EmptyHeaderTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
        => SelectGroupHeaderTemplate(item);

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => SelectGroupHeaderTemplate(item);

    private DataTemplate SelectGroupHeaderTemplate(object item)
    {
        if (item is IReadOnlyObservableGroup group
            && group.Key is MailListGroupKey { IsGroupless: true })
        {
            return EmptyHeaderTemplate ?? throw new ArgumentNullException(nameof(EmptyHeaderTemplate));
        }

        return DefaultHeaderTemplate ?? throw new ArgumentNullException(nameof(DefaultHeaderTemplate));
    }
}
