using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

public sealed class ProviderSelectionPage : NSView
{
    public ProviderSelectionPage(params NSView[] controls) => Wino.Presentation.AppKit.Layout.Fill(Wino.Presentation.AppKit.Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, controls), this, 28);
}
