using AppKit;
using Foundation;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Tab moves between the fields of every sheet (sign-in, form sheets, pickers, alerts with fields).
/// AppKit builds a window's key-view loop by itself only when the window has no initial first
/// responder and never rebuilds it unless asked, so sheets that focus a field on open, or show
/// and hide rows, had no Tab order. Each sheet that becomes key recalculates its loop from the
/// layout, top to bottom. Windows with hand-chained loops (compose, onboarding, IMAP settings)
/// are never sheets and keep their chains.
/// </summary>
internal static class SheetKeyViewLoop
{
    private static NSObject? _observer;

    public static void Install()
    {
        _observer ??= NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidBecomeKeyNotification, notification =>
        {
            if (notification.Object is not NSWindow { IsSheet: true } sheet || sheet.AutorecalculatesKeyViewLoop) return;
            sheet.AutorecalculatesKeyViewLoop = true;
            sheet.RecalculateKeyViewLoop();
        });
    }
}
