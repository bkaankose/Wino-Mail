using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Selectors;

public partial class CustomWinoMessageDialogIconSelector : DataTemplateSelector
{
    public DataTemplate InfoIconTemplate { get; set; } = null!;
    public DataTemplate WarningIconTemplate { get; set; } = null!;
    public DataTemplate QuestionIconTemplate { get; set; } = null!;
    public DataTemplate ErrorIconTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item == null) return InfoIconTemplate;

        if (item is WinoCustomMessageDialogIcon icon)
        {
            switch (icon)
            {
                case WinoCustomMessageDialogIcon.Information:
                    return InfoIconTemplate;
                case WinoCustomMessageDialogIcon.Warning:
                    return WarningIconTemplate;
                case WinoCustomMessageDialogIcon.Error:
                    return ErrorIconTemplate;
                case WinoCustomMessageDialogIcon.Question:
                    return QuestionIconTemplate;
                default:
                    throw new Exception("Unknown custom message dialog icon.");
            }
        }
        return base.SelectTemplateCore(item, container) ?? InfoIconTemplate;
    }
}
