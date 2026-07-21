using System.Runtime.InteropServices;
using System.Text;

namespace Wino.Companion.Services;

/// <summary>
/// Bridges the synchronous INativeAppService HWND callback used by existing
/// authenticators to the validated UWP CoreWindow handle supplied on the
/// current AppService request.
/// </summary>
internal sealed class CompanionWindowHandleProvider(CompanionAppService appService)
{
    private const string ApplicationFrameWindowClass = "ApplicationFrameWindow";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    public nint GetWindowHandle()
    {
        var client = appService.GetActiveClient();
        if (client is null)
        {
            return nint.Zero;
        }

        if (InteractiveWindowBroker.IsValidForProcess(client.WindowHandle, client.ProcessId))
        {
            return client.WindowHandle;
        }

        // A request can arrive before XAML supplies a handle. The full-trust companion
        // can still recover the CoreWindow by walking ApplicationFrameHost's HWND tree
        // and matching the request's validated UWP PID.
        return FindUwpCoreWindowHandle((uint)client.ProcessId);
    }

    private static nint FindUwpCoreWindowHandle(uint processId)
    {
        for (var applicationFrame = FindWindowEx(
                 nint.Zero,
                 nint.Zero,
                 ApplicationFrameWindowClass,
                 null);
             applicationFrame != nint.Zero;
             applicationFrame = FindWindowEx(
                 nint.Zero,
                 applicationFrame,
                 ApplicationFrameWindowClass,
                 null))
        {
            var result = nint.Zero;
            EnumChildWindows(applicationFrame, (windowHandle, _) =>
            {
                var className = new StringBuilder(128);
                _ = GetClassName(windowHandle, className, className.Capacity);
                if (!string.Equals(className.ToString(), CoreWindowClass, StringComparison.Ordinal))
                {
                    return true;
                }

                GetWindowThreadProcessId(windowHandle, out var owningProcessId);
                if (owningProcessId != processId)
                {
                    return true;
                }

                result = windowHandle;
                return false;
            }, nint.Zero);

            if (result != nint.Zero)
            {
                return result;
            }
        }

        for (var coreWindow = FindWindowEx(nint.Zero, nint.Zero, CoreWindowClass, null);
             coreWindow != nint.Zero;
             coreWindow = FindWindowEx(nint.Zero, coreWindow, CoreWindowClass, null))
        {
            GetWindowThreadProcessId(coreWindow, out var owningProcessId);
            if (owningProcessId == processId)
            {
                return coreWindow;
            }
        }

        return nint.Zero;
    }

    private delegate bool EnumWindowProc(nint windowHandle, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowEx(
        nint parentWindow,
        nint childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        nint parentWindow,
        EnumWindowProc callback,
        nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
