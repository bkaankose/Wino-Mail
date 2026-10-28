#if DEBUG
namespace Wino.Presentation.UIKit;

/// <summary>
/// Weak registry of page and cell instances for leak checks (the UIKit twin of the AppKit registry): the debug bridge
/// forces a collection and counts what is still alive per type. Tracking is a weak reference per instance.
/// </summary>
public static class WinoLiveObjects
{
    private static readonly List<WeakReference<object>> _tracked = new();

    public static void Track(object instance)
    {
        lock (_tracked)
        {
            if (_tracked.Count > 4096) _tracked.RemoveAll(reference => !reference.TryGetTarget(out _));
            _tracked.Add(new WeakReference<object>(instance));
        }
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

    /// <summary>
    /// Breadth-first search over instance fields, collections and delegate targets from <paramref name="root"/> to
    /// <paramref name="target"/>; returns the field path or null. Debug leak hunting only.
    /// </summary>
    public static string? PathTo(object root, object target, int maxDepth = 7)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Node, string Path, int Depth)>();
        queue.Enqueue((root, root.GetType().Name, 0));
        seen.Add(root);
        while (queue.Count > 0)
        {
            var (node, path, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;
            foreach (var (name, value) in Children(node))
            {
                if (value is null || value is string || value.GetType().IsPrimitive || value is Type || value is System.Reflection.MemberInfo) continue;
                if (ReferenceEquals(value, target)) return $"{path}.{name}";
                if (!seen.Add(value)) continue;
                queue.Enqueue((value, $"{path}.{name}<{value.GetType().Name}>", depth + 1));
            }
        }
        return null;
    }

    private static IEnumerable<(string Name, object? Value)> Children(object node)
    {
        if (node is Delegate handler)
        {
            foreach (var entry in handler.GetInvocationList()) yield return ("target", entry.Target);
            yield break;
        }
        // Enumerating a native collection (subviews, NSArray) runs native code on objects that may be disposed.
        if (node is System.Collections.IEnumerable items and not System.Collections.IDictionary and not Foundation.NSObject)
        {
            int index = 0;
            foreach (var item in items) { if (index > 200) break; yield return ($"[{index++}]", item); }
        }
        for (var type = node.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsPointer) continue;
                object? value;
                try { value = field.GetValue(node); } catch { continue; }
                yield return (field.Name, value);
            }
    }
}
#endif
