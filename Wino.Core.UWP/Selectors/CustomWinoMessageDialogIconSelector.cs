using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Core.UWP.Selectors;

public partial class CustomWinoMessageDialogIconSelector : DataTemplateSelector
{
    public DataTemplate InfoIconTemplate { get; set; }
    public DataTemplate WarningIconTemplate { get; set; }
    public DataTemplate QuestionIconTemplate { get; set; }
    public DataTemplate ErrorIconTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item == null) return null;

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
        return base.SelectTemplateCore(item, container);
    }
}
