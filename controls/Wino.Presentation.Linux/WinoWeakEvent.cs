namespace Wino.Presentation.Linux;

/// <summary>
/// A static notification that holds its listeners weakly. Recycled list cells register here instead of on a static
/// C# event: a static event roots every listener until it unsubscribes, which a recycled cell never does, so discarded
/// cells leaked and kept running handlers. Register with a static lambda that receives the target, so nothing is captured.
/// </summary>
public sealed class WinoWeakEvent
{
    private abstract class Listener
    {
        public abstract bool IsAlive { get; }
        public abstract bool Is(object target);
        public abstract void Invoke();
    }

    private sealed class Listener<T>(T target, Action<T> handler) : Listener where T : class
    {
        private readonly WeakReference<T> _target = new(target);

        public override bool IsAlive => _target.TryGetTarget(out _);
        public override bool Is(object target) => _target.TryGetTarget(out var live) && ReferenceEquals(live, target);
        public override void Invoke() { if (_target.TryGetTarget(out var live)) handler(live); }
    }

    private readonly List<Listener> _listeners = [];

    public void Add<T>(T target, Action<T> handler) where T : class
    {
        lock (_listeners) _listeners.Add(new Listener<T>(target, handler));
    }

    public void Remove(object target)
    {
        lock (_listeners) _listeners.RemoveAll(listener => !listener.IsAlive || listener.Is(target));
    }

    /// <summary>Invokes every live listener on the calling thread and drops collected ones.</summary>
    public void Raise()
    {
        Listener[] live;
        lock (_listeners)
        {
            _listeners.RemoveAll(static listener => !listener.IsAlive);
            live = [.. _listeners];
        }
        foreach (var listener in live) listener.Invoke();
    }
}
