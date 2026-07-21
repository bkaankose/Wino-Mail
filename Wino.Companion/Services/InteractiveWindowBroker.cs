using System.Runtime.InteropServices;

namespace Wino.Companion.Services;

internal static class InteractiveWindowBroker
{
    public static bool IsValidForProcess(nint windowHandle, int expectedProcessId)
    {
        if (windowHandle == nint.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        GetWindowThreadProcessId(windowHandle, out var owningProcessId);
        return owningProcessId == (uint)expectedProcessId;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
