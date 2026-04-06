using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Wino.Mail.WinUI.Helpers;

internal static partial class InstalledPrinterHelper
{
    private const int PrinterEnumLocal = 0x00000002;
    private const int PrinterEnumConnections = 0x00000004;

    public static IReadOnlyList<string> GetInstalledPrinters()
    {
        var flags = PrinterEnumLocal | PrinterEnumConnections;

        if (!EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out var bytesNeeded, out _))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 122 || bytesNeeded <= 0)
            {
                throw new InvalidOperationException($"EnumPrinters failed to query buffer size. Win32 error: {error}.");
            }
        }

        var printerBuffer = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            if (!EnumPrinters(flags, null, 4, printerBuffer, bytesNeeded, out _, out var printerCount))
            {
                throw new InvalidOperationException($"EnumPrinters failed to enumerate printers. Win32 error: {Marshal.GetLastWin32Error()}.");
            }

            var printers = new List<string>(printerCount);
            var structSize = Marshal.SizeOf<PrinterInfo4>();

            for (var i = 0; i < printerCount; i++)
            {
                var current = IntPtr.Add(printerBuffer, i * structSize);
                var info = Marshal.PtrToStructure<PrinterInfo4>(current);
                if (!string.IsNullOrWhiteSpace(info.PrinterName))
                {
                    printers.Add(info.PrinterName);
                }
            }

            return printers;
        }
        finally
        {
            Marshal.FreeHGlobal(printerBuffer);
        }
    }

    [LibraryImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumPrinters(
        int flags,
        string? name,
        int level,
        IntPtr pPrinterEnum,
        int cbBuf,
        out int pcbNeeded,
        out int pcReturned);

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo4
    {
        public nint PrinterNamePointer;
        public nint ServerNamePointer;
        public int Attributes;

        public string? PrinterName => Marshal.PtrToStringUni(PrinterNamePointer);
    }
}
