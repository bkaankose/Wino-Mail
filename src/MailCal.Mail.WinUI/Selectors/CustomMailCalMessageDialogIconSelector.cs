using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Enums;

namespace MailCal.Mail.WinUI.Selectors;

public partial class CustomMailCalMessageDialogIconSelector : DataTemplateSelector
{
    public DataTemplate InfoIconTemplate { get; set; } = null!;
    public DataTemplate WarningIconTemplate { get; set; } = null!;
    public DataTemplate QuestionIconTemplate { get; set; } = null!;
    public DataTemplate ErrorIconTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item == null) return InfoIconTemplate;

        if (item is MailCalCustomMessageDialogIcon icon)
        {
            switch (icon)
            {
                case MailCalCustomMessageDialogIcon.Information:
                    return InfoIconTemplate;
                case MailCalCustomMessageDialogIcon.Warning:
                    return WarningIconTemplate;
                case MailCalCustomMessageDialogIcon.Error:
                    return ErrorIconTemplate;
                case MailCalCustomMessageDialogIcon.Question:
                    return QuestionIconTemplate;
                default:
                    throw new Exception("Unknown custom message dialog icon.");
            }
        }
        return base.SelectTemplateCore(item, container) ?? InfoIconTemplate;
    }
}
