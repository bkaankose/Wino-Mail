using System.Diagnostics;
using System.Threading;

namespace Wino.Mail.WinUI;

internal static class LaunchPerformanceTracker
{
    private static readonly Stopwatch Stopwatch = new();
    private static int _started;
    private static int _reportedShellLoaded;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        Stopwatch.Restart();
        Debug.WriteLine("[Wino startup] Process started.");
    }

    public static void Mark(string checkpoint)
    {
        if (Volatile.Read(ref _started) == 0 ||
            Volatile.Read(ref _reportedShellLoaded) == 1)
        {
            return;
        }

        Debug.WriteLine($"[Wino startup] {checkpoint}: {Stopwatch.ElapsedMilliseconds} ms");
    }

    public static void ReportShellLoaded()
    {
        if (Volatile.Read(ref _started) == 0 ||
            Interlocked.Exchange(ref _reportedShellLoaded, 1) == 1)
        {
            return;
        }

        Debug.WriteLine($"[Wino startup] WinoAppShell Loaded: {Stopwatch.ElapsedMilliseconds} ms");
    }
}
