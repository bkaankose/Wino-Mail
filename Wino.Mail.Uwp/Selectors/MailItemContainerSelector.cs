using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Uwp.Selectors;

public partial class MailItemContainerSelector : DataTemplateSelector
{
    public DataTemplate? EmailTemplate { get; set; }
    public DataTemplate? ThreadExpanderTemplate { get; set; }
    public DataTemplate? DateGroupHeaderTemplate { get; set; }
    public DataTemplate? SenderGroupHeaderTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        return item switch
        {
            MailItemViewModel => EmailTemplate ?? throw new ArgumentNullException(nameof(EmailTemplate)),
            ThreadMailItemViewModel => ThreadExpanderTemplate ?? throw new ArgumentNullException(nameof(ThreadExpanderTemplate)),
            DateGroupHeader => DateGroupHeaderTemplate ?? throw new ArgumentNullException(nameof(DateGroupHeaderTemplate)),
            SenderGroupHeader => SenderGroupHeaderTemplate ?? throw new ArgumentNullException(nameof(SenderGroupHeaderTemplate)),
            _ => base.SelectTemplateCore(item)
        };
    }
}
