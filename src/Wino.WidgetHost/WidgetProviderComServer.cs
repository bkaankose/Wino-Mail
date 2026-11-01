using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Windows.Widgets.Providers;

namespace Wino.WidgetHost;

/// <summary>
/// Registers the class factory the Widgets Board activates. The factory is a source-generated COM class;
/// the provider it hands out is a CsWinRT object, so the Board receives a WinRT CCW for IWidgetProvider.
/// </summary>
internal sealed partial class WidgetProviderComServer : IDisposable
{
    private const uint ClsctxLocalServer = 0x4;
    private const uint RegclsMultipleUse = 0x1;
    private const int ClassENoAggregation = unchecked((int)0x80040110);
    private const int ENoInterface = unchecked((int)0x80004002);
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private readonly WidgetProviderClassFactory _factory;
    private uint _registrationCookie;

    public WidgetProviderComServer(Guid classId, IWidgetProvider provider)
    {
        _factory = new WidgetProviderClassFactory(provider);
        var factoryPointer = ComWrappers.GetOrCreateComInterfaceForObject(_factory, CreateComInterfaceFlags.None);

        try
        {
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(
                in classId,
                factoryPointer,
                ClsctxLocalServer,
                RegclsMultipleUse,
                out _registrationCookie));
        }
        finally
        {
            Marshal.Release(factoryPointer);
        }
    }

    public void Dispose()
    {
        if (_registrationCookie == 0)
            return;

        _ = CoRevokeClassObject(_registrationCookie);
        _registrationCookie = 0;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(
        in Guid classId,
        nint classFactory,
        uint classContext,
        uint flags,
        out uint registrationCookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint registrationCookie);

    [GeneratedComInterface]
    [Guid("00000001-0000-0000-C000-000000000046")]
    internal partial interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(nint outer, in Guid interfaceId, out nint instance);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
    }

    [GeneratedComClass]
    internal sealed partial class WidgetProviderClassFactory(IWidgetProvider provider) : IClassFactory
    {
        public int CreateInstance(nint outer, in Guid interfaceId, out nint instance)
        {
            instance = 0;
            if (outer != 0)
                return ClassENoAggregation;

            var inspectable = WinRT.MarshalInterface<IWidgetProvider>.FromManaged(provider);
            if (inspectable == 0)
                return ENoInterface;

            try
            {
                return Marshal.QueryInterface(inspectable, in interfaceId, out instance);
            }
            catch
            {
                instance = 0;
                return ENoInterface;
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }

        public int LockServer(bool lockServer) => 0;
    }
}
