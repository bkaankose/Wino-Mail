using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core;

namespace Wino.Mail.Controls.MailListView;

internal sealed partial class MailListRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SingleItemTemplate { get; set; }

    public DataTemplate? ThreadHeaderTemplate { get; set; }

    public DataTemplate? ThreadChildTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        MailListRow { Kind: MailListRowKind.ThreadHead } => ThreadHeaderTemplate ?? SingleItemTemplate,
        MailListRow { Kind: MailListRowKind.ThreadChild } => ThreadChildTemplate ?? SingleItemTemplate,
        MailListRow => SingleItemTemplate,
        _ => base.SelectTemplateCore(item),
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
