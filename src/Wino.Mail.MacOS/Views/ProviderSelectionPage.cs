using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

public sealed class ProviderSelectionPage : NSView
{
    public ProviderSelectionPage(params NSView[] controls) => Layout.Fill(Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, controls), this, 28);
}
