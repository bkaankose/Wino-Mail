using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Models.MailItem;
using MailCal.Mail.ViewModels.Collections;
using MailCal.Mail.WinUI.Selectors;
using global::MailCal.Mail.Controls.Core;

namespace MailCal.Mail.WinUI.Controls.ListView;

public partial class MailCalGroupHeaderTemplateSelector : DataTemplateSelector, IReleasableTemplateSelector
{
    private bool _isReleased;

    public DataTemplate? DefaultHeaderTemplate { get; set; }
    public DataTemplate? EmptyHeaderTemplate { get; set; }

    public void ReleaseTemplates()
    {
        _isReleased = true;
        DefaultHeaderTemplate = null;
        EmptyHeaderTemplate = null;
    }

    protected override DataTemplate SelectTemplateCore(object item)
        => _isReleased ? base.SelectTemplateCore(item) : SelectGroupHeaderTemplate(item);

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => _isReleased ? base.SelectTemplateCore(item, container) : SelectGroupHeaderTemplate(item);

    private DataTemplate SelectGroupHeaderTemplate(object item)
    {
        if (item is MailListGroup { Key: "" })
        {
            return EmptyHeaderTemplate ?? throw new ArgumentNullException(nameof(EmptyHeaderTemplate));
        }

        return DefaultHeaderTemplate ?? throw new ArgumentNullException(nameof(DefaultHeaderTemplate));
    }
}
