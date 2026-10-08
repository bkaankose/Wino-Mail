using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Mail.MacOS.Views.Shell;

namespace Wino.Mail.MacOS.Views.Extras;

/// <summary>
/// The fixed-size What's New window (Windows WhatsNewWindow, 880×640, close button only). The theme
/// backdrop paints the whole window and the page sits in a Wino zone under the traffic lights.
/// </summary>
public sealed class WhatsNewWindowController : NSWindowController
{
    public const double FixedWidth = 880;
    public const double FixedHeight = 640;

    private readonly NSView _backdrop = new ThemeBackdropView();
    private NSViewController? _page;

    public WhatsNewWindowController() : base(CreateWindow())
    {
        var window = Window!;
        window.ContentView = _backdrop;
        window.WillClose += (_, _) => Closing?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Closing;

    public void SetPage(NSViewController page)
    {
        ClearPage();
        _page = page;
        var view = page.View;
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _backdrop.AddSubview(view);
        NSLayoutConstraint.ActivateConstraints(
        [
            view.LeadingAnchor.ConstraintEqualTo(_backdrop.LeadingAnchor),
            view.TrailingAnchor.ConstraintEqualTo(_backdrop.TrailingAnchor),
            view.TopAnchor.ConstraintEqualTo(_backdrop.TopAnchor),
            view.BottomAnchor.ConstraintEqualTo(_backdrop.BottomAnchor)
        ]);
    }

    public void ClearPage()
    {
        _page?.View.RemoveFromSuperview();
        _page = null;
    }

    public void Present()
    {
        var window = Window!;
        if (!window.IsVisible) window.Center();
        NSApplication.SharedApplication.Activate();
        window.MakeKeyAndOrderFront(this);
    }

    private static NSWindow CreateWindow() => new WhatsNewWindow(new CGRect(0, 0, FixedWidth, FixedHeight),
        NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.FullSizeContentView, NSBackingStore.Buffered, false)
    {
        ReleasedWhenClosed = false,
        Title = Translator.WhatsNew_WindowTitle,
        TitleVisibility = NSWindowTitleVisibility.Hidden,
        TitlebarAppearsTransparent = true,
        MovableByWindowBackground = true,
        TabbingMode = NSWindowTabbingMode.Disallowed,
        ContentMinSize = new CGSize(FixedWidth, FixedHeight),
        ContentMaxSize = new CGSize(FixedWidth, FixedHeight)
    };

    /// <summary>Escape and Cmd+W close the window.</summary>
    private sealed class WhatsNewWindow(CGRect contentRect, NSWindowStyle style, NSBackingStore backing, bool defer)
        : NSWindow(contentRect, style, backing, defer)
    {
        [Export("cancelOperation:")]
        public void CancelOperation(NSObject? sender) => PerformClose(this);

        public override bool PerformKeyEquivalent(NSEvent theEvent)
        {
            var flags = theEvent.ModifierFlags & NSEventModifierMask.DeviceIndependentModifierFlagsMask;
            if (flags == NSEventModifierMask.CommandKeyMask && string.Equals(theEvent.CharactersIgnoringModifiers, "w", StringComparison.OrdinalIgnoreCase))
            {
                PerformClose(this);
                return true;
            }
            return base.PerformKeyEquivalent(theEvent);
        }

        public override void KeyDown(NSEvent theEvent)
        {
            if (theEvent.KeyCode == 53) { PerformClose(this); return; }
            base.KeyDown(theEvent);
        }
    }
}
