using System.Runtime.CompilerServices;

namespace Wino.Presentation.AppKit;

/// <summary>
/// Weak registry of view instances for leak checks: the debug bridge forces a collection and counts
/// what is still alive per type. Tracking is a weak reference per instance and costs nothing otherwise.
/// </summary>
public static class WinoLiveObjects
{
    private static readonly List<WeakReference<object>> _tracked = new();

    public static void Track(object instance)
    {
        lock (_tracked) _tracked.Add(new WeakReference<object>(instance));
    }

    /// <summary>Collects twice, then returns the live instances grouped by type name; the second count is instances whose native peer is already disposed (rooted by managed references only).</summary>
    public static IReadOnlyList<(string Type, int Count, int Disposed)> Snapshot()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        lock (_tracked)
        {
            _tracked.RemoveAll(reference => !reference.TryGetTarget(out _));
            return _tracked.Select(reference => reference.TryGetTarget(out var target) ? target : null)
                .Where(target => target is not null)
                .GroupBy(target => target!.GetType().Name)
                .Select(group => (group.Key, group.Count(), group.Count(target => target is Foundation.NSObject { Handle: var handle } && handle == IntPtr.Zero)))
                .OrderByDescending(entry => entry.Item2)
                .ToList();
        }
    }

    /// <summary>The live tracked instances of a type (for root inspection).</summary>
    public static IReadOnlyList<object> Instances(string typeName)
    {
        lock (_tracked)
            return _tracked.Select(reference => reference.TryGetTarget(out var target) ? target : null)
                .Where(target => target is not null && target.GetType().Name == typeName).Select(target => target!).ToList();
    }

    /// <summary>Delegate targets subscribed to an event on <paramref name="owner"/> (null owner for static events), by type name.</summary>
    public static IEnumerable<string> EventTargets(Type declaring, object? owner, string eventName)
    {
        var field = declaring.GetField(eventName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        if (field?.GetValue(owner) is not Delegate handler) return Array.Empty<string>();
        return handler.GetInvocationList().Select(entry => entry.Target?.GetType().FullName ?? "static").ToList();
    }
}
