using Wino.Core.Domain;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Mail.MacOS.Views;

namespace Wino.Mail.MacOS;

public sealed class WelcomeWindow : NSWindow
{
    private static readonly CGSize DefaultContentSize = new(720, 640);
    private readonly SetupWindowDelegate _windowDelegate = new();
    public WelcomeWindow(NSViewController controller) : base(new CGRect(CGPoint.Empty, DefaultContentSize),
        NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable | NSWindowStyle.FullSizeContentView,
        NSBackingStore.Buffered, false)
    {
        Title = "Wino Mail";
        // Onboarding content runs under a transparent title bar, as in the design boards.
        TitlebarAppearsTransparent = true;
        TitleVisibility = NSWindowTitleVisibility.Hidden;
        ContentMinSize = new CGSize(640, 560);
        ContentViewController = controller;
        SetContentSize(DefaultContentSize);
        Delegate = _windowDelegate;
        ReleaseWhenClosed(false);
        Center();
    }

    /// <summary>
    /// Shows the next setup page at the window's current size. Assigning ContentViewController resizes the window to
    /// the incoming view's frame, which differs per page and can fall below ContentMinSize.
    /// </summary>
    public void ShowPage(NSViewController controller)
    {
        var frame = Frame;
        ContentViewController = controller;
        SetFrame(frame, true);
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
            if (sender is NSWindow { ContentViewController: AccountSetupProgressPageViewController { HasPendingWork: true } or Views.Onboarding.AccountSetupPageViewController { HasPendingWork: true } } window)
            {
                var alert = new NSAlert { MessageText = "Wino Mail", InformativeText = Translator.MacOS_Welcome_CloseBlocked };
                alert.AddButton(Translator.Buttons_OK);
                alert.BeginSheet(window, _ => alert.Dispose());
                return false;
            }
            return true;
        }
    }
}
