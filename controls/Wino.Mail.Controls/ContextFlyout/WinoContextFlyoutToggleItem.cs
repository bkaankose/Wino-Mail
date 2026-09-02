using CommunityToolkit.WinUI;

namespace Wino.Mail.Controls.ContextFlyout;

public partial class WinoContextFlyoutToggleItem : WinoContextFlyoutItem
{
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsChecked { get; set; }
}
