using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Mail.MacOS.Views;

namespace Wino.Mail.MacOS;

public sealed class WelcomeWindow : NSWindow
{
    private readonly SetupWindowDelegate _windowDelegate = new();
    public WelcomeWindow(NSViewController controller) : base(new CGRect(0, 0, 720, 640),
        NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable,
        NSBackingStore.Buffered, false)
    {
        Title = "Wino Mail";
        ContentViewController = controller;
        Delegate = _windowDelegate;
        ReleaseWhenClosed(false);
        Center();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Delegate = null; _windowDelegate.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class SetupWindowDelegate : NSWindowDelegate
    {
        public override bool WindowShouldClose(NSObject sender)
        {
            if (sender is NSWindow { ContentViewController: AccountSetupProgressPageViewController { HasPendingWork: true } } window)
            {
                var alert = new NSAlert { MessageText = "Wino Mail", InformativeText = "Finish or cancel account setup before closing this window." };
                alert.AddButton("OK");
                alert.BeginSheet(window, _ => alert.Dispose());
                return false;
            }
            return true;
        }
    }
}
