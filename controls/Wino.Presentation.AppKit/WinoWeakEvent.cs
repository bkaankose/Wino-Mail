using Foundation;

namespace Wino.Presentation.AppKit;

/// <summary>
/// A static notification that holds its listeners weakly. Reusable views (table cells, tiles, chips)
/// register here instead of on a static C# event: a static event roots every listener until Dispose,
/// which the platform never calls for pooled cells, so they leaked and kept running handlers after
/// they were discarded. Register with a static lambda that receives the target, so nothing is captured.
/// </summary>
public sealed class WinoWeakEvent
{
    private abstract class Listener
    {
        public abstract bool IsAlive { get; }
        public abstract bool Is(object target);
        public abstract void Invoke();
    }

    /// <summary>Holds the target weakly and the handler as a plain delegate, with no closure that could capture the target.</summary>
    private sealed class Listener<T>(T target, Action<T> handler) : Listener where T : class
    {
        private readonly WeakReference<T> _target = new(target);
        private readonly Action<T> _handler = handler;

        public override bool IsAlive => _target.TryGetTarget(out var live) && !(live is NSObject native && native.Handle == IntPtr.Zero);
        public override bool Is(object target) => _target.TryGetTarget(out var live) && ReferenceEquals(live, target);
        public override void Invoke() { if (_target.TryGetTarget(out var live)) _handler(live); }
    }

    private readonly List<Listener> _listeners = new();

    /// <summary>Adds <paramref name="target"/>; <paramref name="handler"/> must be a static lambda taking the target.</summary>
    public void Add<T>(T target, Action<T> handler) where T : class
    {
        lock (_listeners) _listeners.Add(new Listener<T>(target, handler));
    }

    public void Remove(object target)
    {
        lock (_listeners) _listeners.RemoveAll(listener => !listener.IsAlive || listener.Is(target));
    }

    /// <summary>Invokes every live listener on the calling thread and drops collected or disposed ones.</summary>
    public void Raise()
    {
        Listener[] live;
        lock (_listeners)
        {
            _listeners.RemoveAll(listener => !listener.IsAlive);
            live = _listeners.ToArray();
        }
        foreach (var listener in live) listener.Invoke();
    }
}
