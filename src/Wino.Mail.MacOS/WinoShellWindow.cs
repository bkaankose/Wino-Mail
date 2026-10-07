using AppKit;
using CoreGraphics;
using Wino.Mail.MacOS.Views;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS;

/// <summary>
/// The main window: full-size content under a unified toolbar, so the shell pane runs to the top
/// edge. While a Wino theme paints a backdrop the title bar is transparent so the wallpaper shows
/// behind the toolbar as on Windows; with the Default theme it keeps the native title bar material.
/// The shell installs its toolbar on attach.
/// </summary>
public sealed class WinoShellWindow : NSWindow
{
    public WinoShellWindow(NSViewController controller) : base(new CGRect(0, 0, 1440, 900),
        NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable | NSWindowStyle.FullSizeContentView,
        NSBackingStore.Buffered, false)
    {
        Title = "Wino Mail";
        TitleVisibility = NSWindowTitleVisibility.Visible;
        ContentMinSize = new CGSize(900, 560);
        ContentViewController = controller;
        ReleaseWhenClosed(false);
        if (controller is WinoAppShellViewController shell) shell.AttachToWindow(this);
        if (!SetFrameUsingName("WinoMainWindow")) Center();
        FrameAutosaveName = "WinoMainWindow";
        ApplyBackdrop();
        WinoStyle.BackdropChanged += BackdropChanged;
    }

    private void BackdropChanged(object? sender, EventArgs args) => ApplyBackdrop();

    private void ApplyBackdrop()
    {
        bool themed = WinoStyle.HasBackdrop;
        TitlebarAppearsTransparent = themed;
        TitlebarSeparatorStyle = themed ? NSTitlebarSeparatorStyle.None : NSTitlebarSeparatorStyle.Automatic;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.BackdropChanged -= BackdropChanged;
        base.Dispose(disposing);
    }
}
