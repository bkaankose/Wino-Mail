using Wino.Presentation.Bindings;

namespace Wino.Presentation.Linux.Lists;

public interface IWinoCell<in TItem> where TItem : class
{
    Gtk.Widget Root { get; }

    /// <summary>Shows <paramref name="item"/>; every subscription goes into <c>binding.Scope</c>, which the factory disposes at unbind.</summary>
    void Bind(TItem item, WinoCellBinding binding);

    void Unbind();
}

/// <summary>What one bind hands to a cell. The factory disposes the scope at unbind.</summary>
public sealed class WinoCellBinding(Gtk.ListItem listItem, BindingScope scope)
{
    public Gtk.ListItem ListItem { get; } = listItem;

    public BindingScope Scope { get; } = scope;

    public uint Position => ListItem.GetPosition();

    public bool IsSelected => ListItem.GetSelected();
}

/// <summary>
/// A <see cref="Gtk.SignalListItemFactory"/> over reusable cells: one cell per list item, bound to one item at a time
/// with a binding scope of its own. The owner must dispose it: the native factory roots its handlers, which reach this
/// object and every cell.
/// </summary>
public sealed class WinoCellFactory<TCell, TItem> : IDisposable
    where TCell : class, IWinoCell<TItem>
    where TItem : class
{
    private readonly Func<TCell> _create;
    private readonly Action<Exception> _error;
    private readonly IDisposable[] _signals;
    private readonly Dictionary<nint, Slot> _slots = [];
    private readonly Dictionary<TItem, Slot> _bound = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TCell, Slot> _cells = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public WinoCellFactory(Func<TCell> create, Action<Exception> error)
    {
        _create = create;
        _error = error;
        Factory = Gtk.SignalListItemFactory.New();
        var factory = Factory;
        _signals =
        [
            GtkSignals.Connect<Gtk.SignalListItemFactory, Gtk.SignalListItemFactory.SetupSignalArgs>(
                h => factory.OnSetup += h, h => factory.OnSetup -= h, (_, args) => Guard(() => OnSetup(args.Object as Gtk.ListItem))),
            GtkSignals.Connect<Gtk.SignalListItemFactory, Gtk.SignalListItemFactory.BindSignalArgs>(
                h => factory.OnBind += h, h => factory.OnBind -= h, (_, args) => Guard(() => OnBind(args.Object as Gtk.ListItem))),
            GtkSignals.Connect<Gtk.SignalListItemFactory, Gtk.SignalListItemFactory.UnbindSignalArgs>(
                h => factory.OnUnbind += h, h => factory.OnUnbind -= h, (_, args) => Guard(() => OnUnbind(args.Object as Gtk.ListItem))),
            GtkSignals.Connect<Gtk.SignalListItemFactory, Gtk.SignalListItemFactory.TeardownSignalArgs>(
                h => factory.OnTeardown += h, h => factory.OnTeardown -= h, (_, args) => Guard(() => OnTeardown(args.Object as Gtk.ListItem))),
        ];
    }

    public Gtk.SignalListItemFactory Factory { get; }

    /// <summary>Runs once per cell after it is created: attach drag sources, context menus and gestures to <c>cell.Root</c> here.</summary>
    public Action<TCell, Gtk.ListItem>? Setup { get; init; }

    /// <summary>Runs once per cell when GTK drops it.</summary>
    public Action<TCell>? Teardown { get; init; }

    /// <summary>Whether an item can be selected and activated; every item when null.</summary>
    public Func<TItem, bool>? IsSelectable { get; init; }

    public int CellCount => _slots.Count;

    public int BoundCount => _bound.Count;

    /// <summary>Binds whose list item held no <typeparamref name="TItem"/>.</summary>
    public int BindMisses { get; private set; }

    public TCell? CellOf(TItem item) => _bound.TryGetValue(item, out var slot) ? slot.Cell : null;

    public TItem? ItemOf(TCell cell) => _cells.TryGetValue(cell, out var slot) ? slot.Item : null;

    public void ForEachBound(Action<TCell, TItem> action)
    {
        foreach (var slot in _bound.Values.ToArray())
        {
            if (slot.Item is { } item) Guard(() => action(slot.Cell, item));
        }
    }

    /// <summary>The bound cell nearest the top of <paramref name="viewport"/> that is still visible in it, and its y offset there.</summary>
    public bool TryGetTop(Gtk.Widget viewport, out TItem? item, out double offset)
    {
        item = null;
        offset = 0;
        var found = false;
        foreach (var slot in _bound.Values)
        {
            if (slot.Item is null || !slot.Cell.Root.ComputeBounds(viewport, out var bounds)) continue;
            var y = bounds.GetY();
            if (y + bounds.GetHeight() <= 0 || (found && y >= offset)) continue;
            item = slot.Item;
            offset = y;
            found = true;
        }
        return found;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var signal in _signals) signal.Dispose();
        foreach (var slot in _slots.Values.ToArray())
        {
            Guard(() => Unbind(slot));
            Guard(() => Teardown?.Invoke(slot.Cell));
        }
        _slots.Clear();
        _bound.Clear();
        _cells.Clear();
    }

    private void OnSetup(Gtk.ListItem? listItem)
    {
        if (listItem is null) return;
        var cell = _create();
        WinoLiveObjects.Track(cell);
        var slot = new Slot(cell);
        _slots[Key(listItem)] = slot;
        _cells[cell] = slot;
        listItem.SetChild(cell.Root);
        Setup?.Invoke(cell, listItem);
    }

    private void OnBind(Gtk.ListItem? listItem)
    {
        if (listItem is null || !_slots.TryGetValue(Key(listItem), out var slot)) return;
        if (ManagedItem.ValueOf<TItem>(listItem.GetItem()) is not { } item)
        {
            BindMisses++;
            return;
        }
        Unbind(slot);
        var selectable = IsSelectable?.Invoke(item) ?? true;
        listItem.SetSelectable(selectable);
        listItem.SetActivatable(selectable);
        var scope = new BindingScope();
        slot.Item = item;
        slot.Scope = scope;
        _bound[item] = slot;
        slot.Cell.Bind(item, new WinoCellBinding(listItem, scope));
    }

    private void OnUnbind(Gtk.ListItem? listItem)
    {
        if (listItem is not null && _slots.TryGetValue(Key(listItem), out var slot)) Unbind(slot);
    }

    private void OnTeardown(Gtk.ListItem? listItem)
    {
        if (listItem is null || !_slots.Remove(Key(listItem), out var slot)) return;
        Unbind(slot);
        _cells.Remove(slot.Cell);
        Teardown?.Invoke(slot.Cell);
        listItem.SetChild(null);
    }

    private void Unbind(Slot slot)
    {
        if (slot.Item is not { } item) return;
        slot.Item = null;
        if (_bound.TryGetValue(item, out var bound) && ReferenceEquals(bound, slot)) _bound.Remove(item);
        var scope = slot.Scope;
        slot.Scope = null;
        try { slot.Cell.Unbind(); }
        finally { scope?.Dispose(); }
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception exception) { _error(exception); }
    }

    private static nint Key(Gtk.ListItem listItem) => listItem.Handle.DangerousGetHandle();

    private sealed class Slot(TCell cell)
    {
        public TCell Cell { get; } = cell;

        public TItem? Item { get; set; }

        public BindingScope? Scope { get; set; }
    }
}

/// <summary>Section headers for a list over <see cref="ListSections{T}"/>. The owner must dispose it, as a cell factory.</summary>
public sealed class WinoHeaderFactory<TItem> : IDisposable where TItem : class
{
    private readonly IDisposable[] _signals;
    private bool _disposed;

    /// <param name="bind">The header widget, the first item of the section and the section's size.</param>
    public WinoHeaderFactory(Func<Gtk.Widget> create, Action<Gtk.Widget, TItem, uint> bind, Action<Exception> error)
    {
        Factory = Gtk.SignalListItemFactory.New();
        var factory = Factory;
        void Guard(Action action)
        {
            try { action(); }
            catch (Exception exception) { error(exception); }
        }
        _signals =
        [
            GtkSignals.Connect<Gtk.SignalListItemFactory, Gtk.SignalListItemFactory.SetupSignalArgs>(
                h => factory.OnSetup += h, h => factory.OnSetup -= h,
                (_, args) => Guard(() => { if (args.Object is Gtk.ListHeader header) header.SetChild(create()); })),
            GtkSignals.Connect<Gtk.SignalListItemFactory, Gtk.SignalListItemFactory.BindSignalArgs>(
                h => factory.OnBind += h, h => factory.OnBind -= h,
                (_, args) => Guard(() =>
                {
                    if (args.Object is Gtk.ListHeader header && header.GetChild() is { } child && ManagedItem.ValueOf<TItem>(header.GetItem()) is { } item)
                        bind(child, item, header.GetNItems());
                })),
            GtkSignals.Connect<Gtk.SignalListItemFactory, Gtk.SignalListItemFactory.TeardownSignalArgs>(
                h => factory.OnTeardown += h, h => factory.OnTeardown -= h,
                (_, args) => Guard(() => { if (args.Object is Gtk.ListHeader header) header.SetChild(null); })),
        ];
    }

    public Gtk.SignalListItemFactory Factory { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var signal in _signals) signal.Dispose();
    }
}
