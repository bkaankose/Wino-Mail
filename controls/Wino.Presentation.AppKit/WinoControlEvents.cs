using AppKit;

namespace Wino.Presentation.AppKit;

/// <summary>
/// Control actions that do not capture their owner. A framework control (NSButton, NSTextField) with a
/// managed event handler keeps its managed wrapper rooted while its native object lives; a handler that
/// captures the owning cell or view then forms a cycle across the native boundary that the collector
/// cannot break, so pooled cells and their pages stayed in memory. The handler here is a static lambda
/// that receives the owner, which is held weakly.
/// </summary>
public static class WinoControlEvents
{
    public static void OnActivated<T>(this NSControl control, T owner, Action<T> handler) where T : class
    {
        var weak = new WeakReference<T>(owner);
        control.Activated += (_, _) => { if (weak.TryGetTarget(out var live)) handler(live); };
    }

    public static void OnActivated<T>(this NSControl control, T owner, Action<T, NSControl> handler) where T : class
    {
        var weak = new WeakReference<T>(owner);
        control.Activated += (sender, _) => { if (weak.TryGetTarget(out var live) && sender is NSControl source) handler(live, source); };
    }
}
