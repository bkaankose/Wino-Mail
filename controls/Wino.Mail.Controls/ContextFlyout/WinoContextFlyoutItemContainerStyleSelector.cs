using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls.ContextFlyout;

public sealed partial class WinoContextFlyoutItemContainerStyleSelector : StyleSelector
{
    public Style? DefaultStyle { get; set; }

    public Style? DestructiveStyle { get; set; }

    public Style? SeparatorStyle { get; set; }

    protected override Style? SelectStyleCore(object item, DependencyObject container)
        => item switch
        {
            WinoContextFlyoutSeparator => SeparatorStyle,
            WinoContextFlyoutItem { IsDestructive: true } => DestructiveStyle,
            _ => DefaultStyle
        };
}
