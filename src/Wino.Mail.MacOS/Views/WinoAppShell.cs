using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

public sealed class WinoAppShell : NSView
{
    public WinoAppShell(NSView sidebar, NSView content)
    {
        sidebar.WidthAnchor.ConstraintEqualTo(280).Active = true;
        var split = Layout.Stack(NSUserInterfaceLayoutOrientation.Horizontal, sidebar, content);
        Layout.Fill(split, this, 12);
    }
}
