namespace Wino.Presentation.Linux;

/// <summary>
/// Weak registry of view instances for leak checks: a Debug action forces a collection and counts what is still alive
/// per type. Tracking is one weak reference per instance and costs nothing otherwise.
/// </summary>
public static class WinoLiveObjects
{
    private static readonly List<WeakReference<object>> _tracked = [];

    public static void Track(object instance)
    {
        lock (_tracked) _tracked.Add(new WeakReference<object>(instance));
    }

    /// <summary>Collects twice, then counts the live instances per type name, most first. For leak checks only: it blocks on full collections.</summary>
    public static (string Type, int Count)[] Snapshot()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        lock (_tracked)
        {
            _tracked.RemoveAll(static reference => !reference.TryGetTarget(out _));
            return [.. Live()
                .GroupBy(static target => target.GetType().Name)
                .Select(static group => (group.Key, group.Count()))
                .OrderByDescending(static entry => entry.Item2)];
        }
    }

    public static object[] Instances(string typeName)
    {
        lock (_tracked) return [.. Live().Where(target => target.GetType().Name == typeName)];
    }

    private static IEnumerable<object> Live()
    {
        foreach (var reference in _tracked)
        {
            if (reference.TryGetTarget(out var target)) yield return target;
        }
    }
}
