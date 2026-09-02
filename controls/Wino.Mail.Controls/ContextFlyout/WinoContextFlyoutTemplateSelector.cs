using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls.ContextFlyout;

public sealed partial class WinoContextFlyoutTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ItemTemplate { get; set; }

    public DataTemplate? ToggleItemTemplate { get; set; }

    public DataTemplate? RadioItemTemplate { get; set; }

    public DataTemplate? SeparatorTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item switch
        {
            WinoContextFlyoutSeparator => SeparatorTemplate,
            WinoContextFlyoutRadioItem => RadioItemTemplate,
            WinoContextFlyoutToggleItem => ToggleItemTemplate,
            _ => ItemTemplate
        };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
