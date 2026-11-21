namespace Wino.Presentation.Linux.Lists;

/// <summary>
/// Section headers through a <see cref="Gtk.SortListModel"/> with a section sorter. Positions in <see cref="Model"/>
/// (and in a selection model or list item over it) are screen positions, not positions of the source store.
/// </summary>
public sealed class ListSections<T> : IDisposable where T : class
{
    private readonly Orders _orders;
    private readonly Gtk.CustomSorter _sectionSorter;
    private readonly Gtk.CustomSorter? _itemSorter;
    private bool _disposed;

    /// <param name="itemOrder">Order inside a section; null keeps the source order (the sort is stable).</param>
    public ListSections(Gio.ListModel source, Comparison<T> sectionOrder, Comparison<T>? itemOrder, Action<Exception> error)
    {
        // Gir.Core roots a sorter's delegate until the native sorter is finalized, and this object holds the sorter:
        // the delegates reach the comparisons only through Orders, which Dispose empties, or this object never dies.
        var orders = new Orders(sectionOrder, itemOrder, error);
        _orders = orders;
        _sectionSorter = Gtk.CustomSorter.New<ManagedItem>((a, b) => orders.Compare(orders.Section, a, b));
        if (itemOrder is not null) _itemSorter = Gtk.CustomSorter.New<ManagedItem>((a, b) => orders.Compare(orders.Item, a, b));
        Model = Gtk.SortListModel.New(source, _itemSorter);
        Model.SetSectionSorter(_sectionSorter);
    }

    public Gtk.SortListModel Model { get; }

    /// <summary>Re-sorts after section membership or order changed while the items stayed the same.</summary>
    public void Invalidate()
    {
        if (_disposed) return;
        _sectionSorter.Changed(Gtk.SorterChange.Different);
        _itemSorter?.Changed(Gtk.SorterChange.Different);
    }

    public T? ItemAt(uint position) => ManagedItem.ValueOf<T>(Model.GetObject(position));

    /// <summary>The screen position of <paramref name="item"/> (by reference), or null. Linear in the item count.</summary>
    public uint? PositionOf(T item)
    {
        var count = Model.GetNItems();
        for (uint position = 0; position < count; position++)
        {
            if (ReferenceEquals(ItemAt(position), item)) return position;
        }
        return null;
    }

    /// <summary>The section containing <paramref name="position"/>: its first position and the position after its last.</summary>
    public (uint Start, uint End) SectionAt(uint position)
    {
        Model.GetSection(position, out var start, out var end);
        return (start, end);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _orders.Clear();
        Model.SetSectionSorter(null);
        Model.SetSorter(null);
        Model.SetModel(null);
    }

    private sealed class Orders(Comparison<T> section, Comparison<T>? item, Action<Exception> error)
    {
        private Action<Exception>? _error = error;

        public Comparison<T>? Section { get; private set; } = section;

        public Comparison<T>? Item { get; private set; } = item;

        public void Clear()
        {
            Section = null;
            Item = null;
            _error = null;
        }

        public int Compare(Comparison<T>? comparison, ManagedItem a, ManagedItem b)
        {
            if (comparison is null || a.Value is not T left || b.Value is not T right) return 0;
            try { return comparison(left, right); }
            catch (Exception exception)
            {
                var error = _error;
                _error = null;
                error?.Invoke(exception);
                return 0;
            }
        }
    }
}
