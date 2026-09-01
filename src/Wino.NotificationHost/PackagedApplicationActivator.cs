using System.Runtime.InteropServices;

namespace Wino.NotificationHost;

internal static class PackagedApplicationActivator
{
    private static readonly Guid ActivationManagerClassId = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid ActivationManagerInterfaceId = new("2E941141-7F97-4756-BA1D-9DECDE894A3D");

    public static unsafe uint Activate(string appUserModelId, string arguments)
    {
        var classId = ActivationManagerClassId;
        var interfaceId = ActivationManagerInterfaceId;
        var result = CoCreateInstance(ref classId, IntPtr.Zero, 5, ref interfaceId, out var instance);
        Marshal.ThrowExceptionForHR(result);

        try
        {
            var virtualTable = *(void***)instance;
            var activateApplication = (delegate* unmanaged[Stdcall]<IntPtr, char*, char*, uint, uint*, int>)virtualTable[3];

            fixed (char* appUserModelIdPointer = appUserModelId)
            fixed (char* argumentsPointer = arguments)
            {
                uint processId = 0;
                result = activateApplication(instance, appUserModelIdPointer, argumentsPointer, 0, &processId);
                Marshal.ThrowExceptionForHR(result);
                return processId;
            }
        }
        finally
        {
            _ = Marshal.Release(instance);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        out IntPtr instance);
}
