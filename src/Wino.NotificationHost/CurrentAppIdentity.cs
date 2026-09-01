using System.Runtime.InteropServices;
using System.Text;

namespace Wino.NotificationHost;

internal static class CurrentAppIdentity
{
    private const int ErrorInsufficientBuffer = 122;

    public static string GetAppUserModelId()
    {
        uint length = 0;
        var result = GetCurrentApplicationUserModelId(ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
            Marshal.ThrowExceptionForHR(HResultFromWin32(result));

        var value = new StringBuilder((int)length);
        result = GetCurrentApplicationUserModelId(ref length, value);
        if (result != 0)
            Marshal.ThrowExceptionForHR(HResultFromWin32(result));

        return value.ToString();
    }

    private static int HResultFromWin32(int error)
        => error <= 0 ? error : unchecked((int)(0x80070000u | (uint)error));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentApplicationUserModelId(ref uint applicationUserModelIdLength, StringBuilder? applicationUserModelId);
}
