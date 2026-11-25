#if DEBUG
using System.Diagnostics;
using Wino.Mail.Controls.Linux.Calendar;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>Timing helpers for the calendar <c>perf</c> verbs: the next relayout, the next painted frame, and a scripted scroll with frame statistics.</summary>
internal static class CalendarPerfProbe
{
    // The app has no synchronization context: every completion below runs its continuation inline on the GTK main
    // thread, so the perf verbs never touch widgets from a pool thread.
    public static Task DelayAsync(uint milliseconds)
    {
        var done = new TaskCompletionSource();
        Wino.Presentation.Linux.MainLoop.Delay(milliseconds, () => done.TrySetResult());
        return done.Task;
    }

    /// <summary>Continues on the main thread after an await that may have left it.</summary>
    public static Task MainAsync()
    {
        var done = new TaskCompletionSource();
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT, () =>
        {
            done.TrySetResult();
            return false;
        });
        return done.Task;
    }

    /// <summary>Completes at the first <see cref="WinoCalendarSurface.Relaid"/> for which <paramref name="accept"/> holds, or with false after the timeout.</summary>
    public static Task<bool> NextRelaidAsync(WinoCalendarSurface surface, Func<bool> accept, int timeoutMs = 5000)
    {
        var done = new TaskCompletionSource<bool>();
        EventHandler? handler = null;
        IDisposable? timer = null;
        handler = (_, _) =>
        {
            if (!accept()) return;
            surface.Relaid -= handler;
            timer?.Dispose();
            Wino.Presentation.Linux.MainLoop.Post(() => done.TrySetResult(true));
        };
        surface.Relaid += handler;
        timer = Wino.Presentation.Linux.MainLoop.Delay((uint)timeoutMs, () =>
        {
            surface.Relaid -= handler;
            done.TrySetResult(false);
        });
        return done.Task;
    }

    /// <summary>Completes after the next frame of the surface's frame clock (the frame that paints the last change); one second at most.</summary>
    public static Task NextFrameAsync(WinoCalendarSurface surface)
    {
        var done = new TaskCompletionSource();
        var fallback = Wino.Presentation.Linux.MainLoop.Delay(1000, () => done.TrySetResult());
        surface.Root.AddTickCallback((_, _) =>
        {
            Wino.Presentation.Linux.MainLoop.Post(() =>
            {
                fallback.Dispose();
                done.TrySetResult();
            });
            return false;
        });
        surface.Root.QueueDraw();
        return done.Task;
    }

    /// <summary>Animates the hour grid from top to bottom in <paramref name="durationMs"/>; returns frame statistics and managed bytes per frame.</summary>
    public static Task<string> ScrollAsync(WinoCalendarSurface surface, int durationMs = 2000)
    {
        var done = new TaskCompletionSource<string>();
        var log = new CalendarFrameLog();
        var adjustment = surface.VerticalAdjustment;
        var range = Math.Max(0, adjustment.GetUpper() - adjustment.GetPageSize());
        var clock = Stopwatch.StartNew();
        long allocated = 0, lastBytes = -1, frames = 0;
        surface.Root.AddTickCallback((_, frameClock) =>
        {
            log.Record(frameClock.GetFrameTime());
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            if (lastBytes >= 0)
            {
                allocated += bytes - lastBytes;
                frames++;
            }
            var progress = Math.Min(1, clock.Elapsed.TotalMilliseconds / durationMs);
            adjustment.SetValue(range * progress);
            lastBytes = GC.GetAllocatedBytesForCurrentThread();
            if (progress < 1) return true;
            var (count, p50, p95, max, over) = log.Summarize();
            done.TrySetResult(FormattableString.Invariant($"frames={count} p50={p50:0.0}ms p95={p95:0.0}ms max={max:0.0}ms over25={over} bytesPerFrame={(frames == 0 ? 0 : allocated / frames)}"));
            return false;
        });
        return done.Task;
    }
}
#endif
