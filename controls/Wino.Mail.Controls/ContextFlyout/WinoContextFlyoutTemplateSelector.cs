using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls.ContextFlyout;

/// <summary>
/// Resolves every context flyout visual. The presenter never composes item visuals itself.
/// </summary>
public sealed partial class WinoContextFlyoutTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ItemTemplate { get; set; }

    public DataTemplate? DestructiveItemTemplate { get; set; }

    public DataTemplate? ToggleItemTemplate { get; set; }

    public DataTemplate? RadioItemTemplate { get; set; }

    public DataTemplate? SubItemTemplate { get; set; }

    public DataTemplate? SeparatorTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is not ContextFlyoutRow row
            ? null
            : row.Kind switch
            {
                ContextFlyoutRowKind.Separator => SeparatorTemplate,
                ContextFlyoutRowKind.SubMenu => SubItemTemplate,
                ContextFlyoutRowKind.Radio => RadioItemTemplate,
                ContextFlyoutRowKind.Toggle => ToggleItemTemplate,
                _ => row.IsDestructive ? DestructiveItemTemplate : ItemTemplate
            };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
