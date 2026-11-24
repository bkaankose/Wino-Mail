using System.Runtime.InteropServices;
using AppKit;
using ObjCRuntime;

namespace Wino.Mail.MacOS.Infrastructure;

internal static class WindowContentRelease
{
    // The binding refuses null for contentViewController although AppKit takes nil, so a closing window clears it
    // through the runtime. Without this the window keeps its page, and the page its whole view tree.
    public static void ClearContentViewController(this NSWindow window)
        => SetObject(window.Handle, Selector.GetHandle("setContentViewController:"), IntPtr.Zero);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SetObject(IntPtr receiver, IntPtr selector, IntPtr value);
}
