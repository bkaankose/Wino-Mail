using UIKit;

namespace Wino.Presentation.UIKit;

/// <summary>
/// Control actions that do not capture their owner. A framework control with a managed event handler
/// keeps its managed wrapper rooted while its native object lives; a handler that captures the owning
/// cell forms a cycle across the native boundary that the collector cannot break, so pooled cells and
/// their pages stayed in memory. The handler is a static lambda that receives the owner, held weakly.
/// </summary>
public static class WinoControlEvents
{
    public static void OnPrimaryAction<T>(this UIControl control, T owner, Action<T> handler) where T : class
    {
        var weak = new WeakReference<T>(owner);
        control.PrimaryActionTriggered += (_, _) => { if (weak.TryGetTarget(out var live)) handler(live); };
    }
}
