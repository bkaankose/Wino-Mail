using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Wino.NotificationHost;

internal sealed partial class NotificationActivationComServer : IDisposable
{
    private const uint ClsctxLocalServer = 0x4;
    private const uint RegclsMultipleUse = 0x1;
    private const int ClassENoAggregation = unchecked((int)0x80040110);
    private const int ENoInterface = unchecked((int)0x80004002);
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private readonly NotificationActivationCallback _callback;
    private readonly NotificationClassFactory _factory;
    private uint _registrationCookie;

    public NotificationActivationComServer(
        Guid classId,
        Action<string, IReadOnlyDictionary<string, string>> activated)
    {
        _callback = new NotificationActivationCallback(activated);
        _factory = new NotificationClassFactory(_callback);
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

    [GeneratedComInterface]
    [Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
    internal partial interface INotificationActivationCallback
    {
        [PreserveSig]
        int Activate(nint appUserModelId, nint invokedArgs, nint data, uint dataCount);
    }

    [GeneratedComClass]
    internal sealed partial class NotificationClassFactory(NotificationActivationCallback callback) : IClassFactory
    {
        public int CreateInstance(nint outer, in Guid interfaceId, out nint instance)
        {
            instance = 0;
            if (outer != 0)
                return ClassENoAggregation;

            var unknown = ComWrappers.GetOrCreateComInterfaceForObject(callback, CreateComInterfaceFlags.None);
            try
            {
                return Marshal.QueryInterface(unknown, in interfaceId, out instance);
            }
            catch
            {
                instance = 0;
                return ENoInterface;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public int LockServer(bool lockServer) => 0;
    }

    [GeneratedComClass]
    internal sealed partial class NotificationActivationCallback(
        Action<string, IReadOnlyDictionary<string, string>> activated) : INotificationActivationCallback
    {
        public int Activate(nint appUserModelId, nint invokedArgs, nint data, uint dataCount)
        {
            try
            {
                var argument = Marshal.PtrToStringUni(invokedArgs) ?? string.Empty;
                var input = new Dictionary<string, string>(checked((int)dataCount), StringComparer.Ordinal);
                var itemSize = Marshal.SizeOf<NotificationUserInputData>();

                for (var index = 0; index < dataCount; index++)
                {
                    var item = Marshal.PtrToStructure<NotificationUserInputData>(data + checked((int)index * itemSize));
                    var key = Marshal.PtrToStringUni(item.Key) ?? string.Empty;
                    var value = Marshal.PtrToStringUni(item.Value) ?? string.Empty;
                    input[key] = value;
                }

                activated(argument, input);
                return 0;
            }
            catch (Exception ex)
            {
                return ex.HResult;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NotificationUserInputData
    {
        public readonly nint Key;
        public readonly nint Value;
    }
}
