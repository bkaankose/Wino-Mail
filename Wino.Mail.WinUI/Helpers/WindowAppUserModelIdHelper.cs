using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using WinUIEx;

namespace Wino.Mail.WinUI.Helpers;

/// <summary>
/// Assigns an explicit AppUserModelID to a window's taskbar identity. Uses
/// source-generated COM interop (GeneratedComInterface/LibraryImport) so the
/// code is trim- and AOT-safe.
/// </summary>
internal static partial class WindowAppUserModelIdHelper
{
    private static readonly Guid PropertyStoreGuid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdPropertyKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    private static readonly StrategyBasedComWrappers ComWrappers = new();

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

            var hr = SHGetPropertyStoreForWindow(hwnd, in PropertyStoreGuid, out var propertyStorePtr);
            if (hr < 0 || propertyStorePtr == IntPtr.Zero)
                return;

            try
            {
                var propertyStore = (IPropertyStore)ComWrappers.GetOrCreateObjectForComInstance(propertyStorePtr, CreateObjectFlags.None);

                var value = PropVariant.FromString(appUserModelId);

                try
                {
                    propertyStore.SetValue(in AppUserModelIdPropertyKey, in value);
                    propertyStore.Commit();
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.Release(propertyStorePtr);
            }
        }
        catch
        {
            // Best effort only. Some Windows builds may keep the original taskbar identity.
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(IntPtr hwnd, in Guid riid, out IntPtr propertyStore);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant propvar);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FormatId { get; } = fmtid;
        public uint PropertyId { get; } = pid;
    }

    /// <summary>
    /// Minimal blittable PROPVARIANT carrying a VT_LPWSTR pointer.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        private const ushort VT_LPWSTR = 31;

        public ushort ValueType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr PointerValue;
        public IntPtr Reserved4;

        public static PropVariant FromString(string value) => new()
        {
            ValueType = VT_LPWSTR,
            PointerValue = Marshal.StringToCoTaskMemUni(value)
        };
    }

    [GeneratedComInterface]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        uint GetCount();
        void GetAt(uint propertyIndex, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropVariant pv);
        void SetValue(in PropertyKey key, in PropVariant pv);
        void Commit();
    }
}
