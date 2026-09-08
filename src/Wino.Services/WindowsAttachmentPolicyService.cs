#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Wino.Services;

internal sealed unsafe class WindowsAttachmentPolicyService : IWindowsAttachmentPolicyService
{
    private static readonly Guid AttachmentServicesClassId = new("4125DD96-E03A-4103-8F70-E0597D803B9C");
    private static readonly Guid AttachmentExecuteInterfaceId = new("73DB1241-1E85-4581-8E4F-A81E1D0F8C57");
    private static readonly Guid WinoClientId = new("8E53B55D-158D-49B3-B93A-682A0015E456");

    public void ApplySavePolicy(string localPath)
    {
        using var attachment = Create(localPath);
        ThrowIfFailed(attachment.Save());
    }

    public void Execute(string localPath, IntPtr ownerWindow)
    {
        using var attachment = Create(localPath);
        ThrowIfFailed(attachment.Execute(ownerWindow));
    }

    private static AttachmentExecuteHandle Create(string localPath)
    {
        var classId = AttachmentServicesClassId;
        var interfaceId = AttachmentExecuteInterfaceId;
        ThrowIfFailed(CoCreateInstance(&classId, 0, 1, &interfaceId, out var instance));

        var handle = new AttachmentExecuteHandle(instance);

        try
        {
            handle.SetClientGuid(WinoClientId);
            handle.SetLocalPath(localPath);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
            throw new WindowsAttachmentPolicyException(hresult);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        Guid* classId,
        nint outer,
        uint context,
        Guid* interfaceId,
        out nint instance);

    private sealed class AttachmentExecuteHandle(nint instance) : IDisposable
    {
        private nint _instance = instance;

        public void SetClientGuid(Guid clientId)
        {
            var vtable = *(nint**)_instance;
            var method = (delegate* unmanaged[Stdcall]<nint, Guid*, int>)vtable[4];
            ThrowIfFailed(method(_instance, &clientId));
        }

        public void SetLocalPath(string localPath)
        {
            var vtable = *(nint**)_instance;
            var method = (delegate* unmanaged[Stdcall]<nint, char*, int>)vtable[5];
            fixed (char* path = localPath)
                ThrowIfFailed(method(_instance, path));
        }

        public int Save()
        {
            var vtable = *(nint**)_instance;
            var method = (delegate* unmanaged[Stdcall]<nint, int>)vtable[11];
            return method(_instance);
        }

        public int Execute(IntPtr ownerWindow)
        {
            var vtable = *(nint**)_instance;
            var method = (delegate* unmanaged[Stdcall]<nint, nint, char*, nint*, int>)vtable[12];
            return method(_instance, ownerWindow, null, null);
        }

        public void Dispose()
        {
            var instance = _instance;
            if (instance == 0)
                return;

            _instance = 0;
            var vtable = *(nint**)instance;
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            _ = release(instance);
        }
    }
}

internal sealed class WindowsAttachmentPolicyException(int hresult)
    : Exception(Marshal.GetExceptionForHR(hresult)?.Message ?? $"Attachment policy failed with HRESULT 0x{hresult:X8}.")
{
    public int HResultCode { get; } = hresult;

    public bool IsCancellation => HResultCode == unchecked((int)0x800704C7);
}
