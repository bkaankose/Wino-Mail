using System.Runtime.InteropServices;

namespace Wino.NotificationHost;

internal static class NotificationHostLifetime
{
    public const int TimeoutExitCode = 2;

    public static void Start(TimeSpan maximumLifetime)
    {
        // This independent thread survives Run returning and does not depend on the thread pool.
        // TerminateProcess also bypasses managed/COM shutdown, which can itself be blocked.
        new Thread(() =>
        {
            Thread.Sleep(maximumLifetime);
            _ = TerminateProcess(new IntPtr(-1), TimeoutExitCode);
        }) { IsBackground = true, Name = "Notification host lifetime" }.Start();
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
