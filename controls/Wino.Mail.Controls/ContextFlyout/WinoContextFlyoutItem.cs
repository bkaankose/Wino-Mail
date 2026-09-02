using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Wino.Mail.Controls.ContextFlyout;

public partial class WinoContextFlyoutItem : WinoContextFlyoutItemBase
{
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Text { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Breadcrumb { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SearchKeywords { get; set; }

    [GeneratedDependencyProperty]
    public partial IconSource? IconSource { get; set; }

    [GeneratedDependencyProperty]
    public partial ICommand? Command { get; set; }

    [GeneratedDependencyProperty]
    public partial object? CommandParameter { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsEnabled { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsDestructive { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string ShortcutText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string AutomationId { get; set; }

    public KeyboardAccelerator? KeyboardAccelerator { get; set; }

    internal Action? BeforeExecute { get; init; }
}
