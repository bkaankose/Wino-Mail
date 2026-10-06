using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Mail.AppKit.Poc.Controllers;

namespace Wino.Mail.AppKit.Poc;

[Register("AppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate
{
    private NSWindow? _window;
    private AboutPageViewController? _aboutPageController;
    private MainWindowDelegate? _windowDelegate;

    public override void DidFinishLaunching(NSNotification notification)
    {
        var application = NSApplication.SharedApplication;
        application.ActivationPolicy = NSApplicationActivationPolicy.Regular;

        _aboutPageController = new AboutPageViewController();
        _window = new NSWindow(
            new CGRect(0, 0, 560, 360),
            NSWindowStyle.Titled |
            NSWindowStyle.Closable |
            NSWindowStyle.Miniaturizable |
            NSWindowStyle.Resizable,
            NSBackingStore.Buffered,
            defer: false)
        {
            Title = Translator.GetTranslatedString("SettingsAbout_Title"),
            ContentViewController = _aboutPageController,
            ReleasedWhenClosed = false
        };

        _windowDelegate = new MainWindowDelegate(OnWindowClosed);
        _window.Delegate = _windowDelegate;
        _window.Center();
        _window.MakeKeyAndOrderFront(null);
        application.ActivateIgnoringOtherApps(true);

        _aboutPageController.Activate();
    }

    public override void WillTerminate(NSNotification notification)
    {
        _aboutPageController?.Dispose();
        _aboutPageController = null;
    }

    private void OnWindowClosed()
    {
        _aboutPageController?.Dispose();
        _aboutPageController = null;
        _window = null;
    }
}

[Register("MainWindowDelegate")]
internal sealed class MainWindowDelegate : NSWindowDelegate
{
    private readonly Action _onClosed;

    public MainWindowDelegate(Action onClosed) => _onClosed = onClosed;

    public override void WillClose(NSNotification notification) => _onClosed();
}
