using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Wino.Mail.WinUI.Services;

internal sealed class NotificationHostProcessSupervisor : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly SafeJobHandle _job;
    private bool _disposed;

    public NotificationHostProcessSupervisor()
    {
        // No name or inheritable security attributes: only this app owns the job handle.
        _job = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (_job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var limits = new JobExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            _job.Dispose();
            throw new Win32Exception(error);
        }
    }

    public async Task<int> SuperviseAsync(Process process, TimeSpan maximumLifetime, CancellationToken cancellationToken)
    {
        // Keep a handle open before using the PID again, preventing PID reuse throughout supervision.
        _ = process.SafeHandle;

        try
        {
            lock (_syncRoot)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!process.HasExited)
                {
                    // AssignProcessToJobObject requires SET_QUOTA and TERMINATE access.
                    using var assignmentHandle = OpenProcess(0x0101, false, (uint)process.Id);
                    if (assignmentHandle.IsInvalid || !AssignProcessToJobObject(_job, assignmentHandle))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (!process.HasExited)
                            throw new Win32Exception(error, "Could not attach the notification host to the application job.");
                    }
                }
            }

            await process.WaitForExitAsync(cancellationToken).WaitAsync(maximumLifetime, cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch
        {
            // Cancellation, timeout, or failed job assignment must not abandon an activated host.
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The job can terminate the process concurrently with cancellation or timeout.
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _disposed = true;
            _job.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass,
        ref JobExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
