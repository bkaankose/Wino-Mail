using System.Collections.Specialized;
using Wino.Core.Domain.Interfaces;

namespace Wino.Presentation.Linux.Lists;

/// <summary>One splice of the store: <c>Removed</c> items at <c>Position</c> replaced by <c>Added</c> items.</summary>
public readonly record struct ListChange(int Position, int Removed, int Added, bool IsAppend, bool IsReset);

/// <summary>
/// Mirrors a managed list into a <see cref="Gio.ListStore"/> of <see cref="ManagedItem"/>s. Source changes are
/// coalesced to one pass per main-loop turn, and each pass is one splice of the range between the common prefix and
/// the common suffix (by reference), so an append stays an append and unchanged rows are not rebound.
/// </summary>
public sealed class ObservableListModel<T> : IDisposable where T : class
{
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private readonly MainLoop.Coalescer _coalescer;
    private readonly List<T> _items = [];
    private readonly List<ManagedItem> _wrappers = [];
    private Dictionary<T, int>? _index;
    private IReadOnlyList<T>? _source;
    private INotifyCollectionChanged? _observed;
    private bool _disposed;

    public ObservableListModel(IDispatcher dispatcher, Action<Exception> error)
    {
        _dispatcher = dispatcher;
        _error = error;
        _coalescer = new MainLoop.Coalescer(ApplySource);
        Store = Gio.ListStore.New(ManagedItem.GetGType());
    }

    /// <summary>Item type <see cref="ManagedItem"/>; give it to a selection model or to <see cref="ListSections{T}"/>.</summary>
    public Gio.ListStore Store { get; }

    public int Count => _items.Count;

    public T this[int index] => _items[index];

    /// <summary>True inside a splice: selection handlers ignore the signals GTK emits then.</summary>
    public bool IsApplying { get; private set; }

    public event Action<ListChange>? Changing;

    public event Action<ListChange>? Changed;

    /// <summary>The index by reference identity, or -1.</summary>
    public int IndexOf(T item)
    {
        if (_index is null)
        {
            _index = new Dictionary<T, int>(_items.Count, ReferenceEqualityComparer.Instance);
            for (var position = 0; position < _items.Count; position++) _index.TryAdd(_items[position], position);
        }
        return _index.TryGetValue(item, out var index) ? index : -1;
    }

    /// <summary>Mirrors <paramref name="source"/> now and follows it when it raises collection changes; replaces a previous source.</summary>
    public void Attach(IReadOnlyList<T> source)
    {
        if (_disposed) return;
        Detach();
        _source = source;
        if (source is INotifyCollectionChanged observed)
        {
            observed.CollectionChanged += OnSourceChanged;
            _observed = observed;
        }
        ApplySource();
    }

    public void Detach()
    {
        if (_observed is not null) _observed.CollectionChanged -= OnSourceChanged;
        _observed = null;
        _source = null;
    }

    /// <summary>Shows a snapshot (for projected lists) at once. A pending source pass still runs and wins.</summary>
    public void SetItems(T[] items)
    {
        if (_disposed) return;
        Apply(items);
    }

    /// <summary>Applies a pending source change now instead of at the next idle.</summary>
    public void Flush() => ApplySource();

    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
        _coalescer.Dispose();
        Store.RemoveAll();
        _items.Clear();
        _wrappers.Clear();
        _index = null;
        Changing = null;
        Changed = null;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (!_disposed) _coalescer.Request();
        });

    private void ApplySource()
    {
        if (_disposed || _source is not { } source) return;
        T[] snapshot;
        try { snapshot = [.. source]; }
        catch (Exception exception)
        {
            _error(exception);
            return;
        }
        Apply(snapshot);
    }

    private void Apply(T[] next)
    {
        var oldCount = _items.Count;
        var shortest = Math.Min(oldCount, next.Length);
        var prefix = 0;
        while (prefix < shortest && ReferenceEquals(_items[prefix], next[prefix])) prefix++;
        var suffix = 0;
        while (suffix < shortest - prefix && ReferenceEquals(_items[oldCount - 1 - suffix], next[next.Length - 1 - suffix])) suffix++;

        var removed = oldCount - prefix - suffix;
        var added = next.Length - prefix - suffix;
        if (removed == 0 && added == 0) return;

        var change = new ListChange(prefix, removed, added, removed == 0 && prefix == oldCount, prefix == 0 && suffix == 0 && oldCount > 0);
        Raise(Changing, change);

        var wrappers = new ManagedItem[added];
        var additions = new GObject.Object[added];
        for (var offset = 0; offset < added; offset++)
        {
            wrappers[offset] = ManagedItem.Create(next[prefix + offset]);
            additions[offset] = wrappers[offset];
        }

        // The managed lists change first, so cells bound during the splice already see the new state.
        _items.RemoveRange(prefix, removed);
        _items.InsertRange(prefix, new ArraySegment<T>(next, prefix, added));
        _wrappers.RemoveRange(prefix, removed);
        _wrappers.InsertRange(prefix, wrappers);
        _index = null;

        IsApplying = true;
        try { Store.Splice((uint)prefix, (uint)removed, additions, (uint)added); }
        catch (Exception exception) { _error(exception); }
        finally { IsApplying = false; }

        Raise(Changed, change);
    }

    private void Raise(Action<ListChange>? handlers, ListChange change)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<ListChange>)handler)(change); }
            catch (Exception exception) { _error(exception); }
        }
    }
}
