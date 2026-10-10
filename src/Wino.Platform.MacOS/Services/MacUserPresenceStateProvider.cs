using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// Presenting (Windows QUNS_PRESENTATION_MODE / QUNS_RUNNING_D3D_FULL_SCREEN): another app is in front
/// with a normal-level window that covers a whole display, as Keynote, PowerPoint, full-screen video and
/// full-screen apps do. Window bounds and owners need no screen recording permission.
/// System quiet time is never reported: macOS Focus modes already hold back Wino's notifications, and
/// reading the Focus state needs a separate entitlement and user authorization.
/// </summary>
public sealed partial class MacUserPresenceStateProvider : IUserPresenceStateProvider
{
    private const string CoreGraphicsLibrary = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const uint OnScreenOnly = 1 << 0;
    private const uint ExcludeDesktopElements = 1 << 4;
    private const uint MaxDisplays = 16;

    public bool IsPresenting()
    {
        try
        {
            var frontmost = NSWorkspace.SharedWorkspace.FrontmostApplication;
            if (frontmost is null || frontmost.ProcessIdentifier == Environment.ProcessId) return false;

            var displays = DisplayBounds();
            if (displays.Count == 0) return false;

            var handle = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktopElements, 0);
            if (handle == IntPtr.Zero) return false;
            using var windows = Runtime.GetNSObject<NSArray>(handle, owns: true);
            if (windows is null) return false;

            for (nuint index = 0; index < windows.Count; index++)
            {
                var window = windows.GetItem<NSDictionary>(index);
                if (Number(window, "kCGWindowOwnerPID") != frontmost.ProcessIdentifier || Number(window, "kCGWindowLayer") != 0) continue;
                if (window["kCGWindowBounds"] is not NSDictionary boundsDictionary) continue;
                if (!CGRect.TryParse(boundsDictionary, out var bounds)) continue;
                if (displays.Any(display => Covers(bounds, display))) return true;
            }
        }
        catch (Exception)
        {
            // Detection is best effort; notifications keep their normal policy when it fails.
        }

        return false;
    }

    public bool IsSystemQuietTimeActive() => false;

    private static bool Covers(CGRect window, CGRect display)
        => window.X <= display.X + 1 && window.Y <= display.Y + 1
           && window.Right >= display.Right - 1 && window.Bottom >= display.Bottom - 1;

    private static int Number(NSDictionary dictionary, string key)
        => dictionary[key] is NSNumber number ? number.Int32Value : -1;

    private static List<CGRect> DisplayBounds()
    {
        var ids = new uint[MaxDisplays];
        if (CGGetActiveDisplayList(MaxDisplays, ids, out var count) != 0) return [];
        return ids.Take((int)count).Select(id => CGDisplay.GetBounds((int)id)).ToList();
    }

    [LibraryImport(CoreGraphicsLibrary)]
    private static partial IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [LibraryImport(CoreGraphicsLibrary)]
    private static partial int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] activeDisplays, out uint displayCount);
}
