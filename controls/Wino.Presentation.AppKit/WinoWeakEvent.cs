using Foundation;

namespace Wino.Presentation.AppKit;

/// <summary>
/// A static notification that holds its listeners weakly. Reusable views (table cells, tiles, chips)
/// register here instead of on a static C# event: a static event roots every listener until Dispose,
/// which AppKit never calls for pooled cells, so they leaked and kept running handlers after they
/// were discarded. Register with a static lambda that receives the target, so the lambda captures nothing.
/// </summary>
public sealed class WinoWeakEvent
{
    private readonly List<(WeakReference<object> Target, Action<object> Handler)> _listeners = new();

    /// <summary>Adds <paramref name="target"/>; <paramref name="handler"/> must not capture it (use a static lambda).</summary>
    public void Add<T>(T target, Action<T> handler) where T : class
    {
        lock (_listeners) _listeners.Add((new WeakReference<object>(target), listener => handler((T)listener)));
    }

    public void Remove(object target)
    {
        lock (_listeners) _listeners.RemoveAll(entry => !entry.Target.TryGetTarget(out var live) || ReferenceEquals(live, target));
    }

    /// <summary>Invokes every live listener on the calling thread and drops collected or disposed ones.</summary>
    public void Raise()
    {
        (object Target, Action<object> Handler)[] live;
        lock (_listeners)
        {
            _listeners.RemoveAll(entry => !entry.Target.TryGetTarget(out var target) || (target is NSObject native && native.Handle == IntPtr.Zero));
            live = _listeners.Select(entry => (entry.Target.TryGetTarget(out var target) ? target : null!, entry.Handler)).Where(entry => entry.Item1 is not null).ToArray();
        }
        foreach (var (target, handler) in live) handler(target);
    }
}
