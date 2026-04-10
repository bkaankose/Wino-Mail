using System;
using System.Runtime.InteropServices;
using WinUIEx;

namespace Wino.Mail.WinUI.Helpers;

internal static class WindowAppUserModelIdHelper
{
    private static readonly Guid PropertyStoreGuid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdPropertyKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    public static void TrySet(WindowEx window, string appUserModelId)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (string.IsNullOrWhiteSpace(appUserModelId))
            return;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero)
                return;

            var propertyStoreGuid = PropertyStoreGuid;
            var appUserModelIdPropertyKey = AppUserModelIdPropertyKey;
            var hr = SHGetPropertyStoreForWindow(hwnd, ref propertyStoreGuid, out var propertyStore);
            if (hr < 0 || propertyStore == null)
                return;

            using (propertyStore)
            {
                using var value = PropVariant.FromString(appUserModelId);
                propertyStore.SetValue(ref appUserModelIdPropertyKey, value);
                propertyStore.Commit();
            }
        }
        catch
        {
            // Best effort only. Some Windows builds may keep the original taskbar identity.
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FormatId { get; } = fmtid;
        public uint PropertyId { get; } = pid;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore : IDisposable
    {
        uint GetCount();
        void GetAt(uint propertyIndex, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Explicit)]
    private sealed class PropVariant : IDisposable
    {
        [FieldOffset(0)]
        private ushort _valueType;

        [FieldOffset(8)]
        private IntPtr _pointerValue;

        private PropVariant(string value)
        {
            _valueType = 31;
            _pointerValue = Marshal.StringToCoTaskMemUni(value);
        }

        public static PropVariant FromString(string value) => new(value);

        public void Dispose()
        {
            PropVariantClear(this);
            GC.SuppressFinalize(this);
        }

        ~PropVariant()
        {
            Dispose();
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear([In, Out] PropVariant propvar);
    }
}
