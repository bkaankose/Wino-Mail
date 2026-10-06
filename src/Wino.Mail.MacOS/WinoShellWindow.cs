using AppKit;
using CoreGraphics;

namespace Wino.Mail.MacOS;

public sealed class WinoShellWindow : NSWindow
{
    public WinoShellWindow(NSViewController controller) : base(new CGRect(0, 0, 1120, 760),
        NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable,
        NSBackingStore.Buffered, false)
    {
        Title = "Wino Mail";
        ContentViewController = controller;
        ReleaseWhenClosed(false);
        Center();
    }
}
