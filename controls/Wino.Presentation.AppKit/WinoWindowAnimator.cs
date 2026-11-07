using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;
using ObjCRuntime;

namespace Wino.Presentation.AppKit;

/// <summary>
/// Animates a window's alpha and frame through its animator proxy, inside an <see cref="NSAnimationContext"/> group.
/// Reading <see cref="NSWindow.Animator"/> on a window whose class is a managed subclass crashes the runtime: AppKit's
/// proxy reports the subclass, and wrapping it reads the proxy as if it were the window. These calls send the messages
/// to the proxy without wrapping it.
/// </summary>
public static class WinoWindowAnimator
{
    private const string ObjCLibrary = "/usr/lib/libobjc.dylib";

    private static readonly IntPtr AnimatorSelector = Selector.GetHandle("animator");
    private static readonly IntPtr SetAlphaValueSelector = Selector.GetHandle("setAlphaValue:");
    private static readonly IntPtr SetFrameDisplaySelector = Selector.GetHandle("setFrame:display:");

    public static void SetAlphaValue(NSWindow window, double alpha)
        => SendDouble(Animator(window), SetAlphaValueSelector, alpha);

    public static void SetFrame(NSWindow window, CGRect frame)
        => SendRectBool(Animator(window), SetFrameDisplaySelector, frame, true);

    private static IntPtr Animator(NSWindow window) => SendIntPtr(window.Handle, AnimatorSelector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendDouble(IntPtr receiver, IntPtr selector, double value);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendRectBool(IntPtr receiver, IntPtr selector, CGRect rect, [MarshalAs(UnmanagedType.I1)] bool flag);
}
