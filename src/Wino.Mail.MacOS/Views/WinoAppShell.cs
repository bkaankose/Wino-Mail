using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

public sealed class WinoAppShell : NSView
{
    public WinoAppShell(NSView sidebar, NSView content)
    {
        sidebar.WidthAnchor.ConstraintEqualTo(280).Active = true;
        var split = Wino.Presentation.AppKit.Layout.Stack(NSUserInterfaceLayoutOrientation.Horizontal, sidebar, content);
        Wino.Presentation.AppKit.Layout.Fill(split, this, 12);
    }
}
