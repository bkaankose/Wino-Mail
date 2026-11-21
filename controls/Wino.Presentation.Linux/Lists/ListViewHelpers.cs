namespace Wino.Presentation.Linux.Lists;

/// <summary>Calls its action whenever the scroller is within a distance of its end; dispose disconnects.</summary>
public sealed class LoadMoreTrigger : IDisposable
{
    private readonly Gtk.Adjustment _adjustment;
    private readonly double _threshold;
    private readonly Action _loadMore;
    private readonly IDisposable _valueChanged;
    private readonly IDisposable _changed;
    private bool _disposed;

    internal LoadMoreTrigger(Gtk.ScrolledWindow scroller, double thresholdPixels, Action loadMore)
    {
        var adjustment = scroller.GetVadjustment();
        _adjustment = adjustment;
        _threshold = thresholdPixels;
        _loadMore = loadMore;
        _valueChanged = GtkSignals.Connect<Gtk.Adjustment>(h => adjustment.OnValueChanged += h, h => adjustment.OnValueChanged -= h, (_, _) => Check());
        _changed = GtkSignals.Connect<Gtk.Adjustment>(h => adjustment.OnChanged += h, h => adjustment.OnChanged -= h, (_, _) => Check());
    }

    /// <summary>Evaluates the distance now, for example after the model changed. The caller's command decides whether loading may run.</summary>
    public void Check()
    {
        if (_disposed) return;
        var upper = _adjustment.GetUpper();
        if (upper > 0 && _adjustment.GetValue() + _adjustment.GetPageSize() >= upper - _threshold) _loadMore();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _valueChanged.Dispose();
        _changed.Dispose();
    }
}

/// <summary>A row and its distance from the top of the scroller, to bring it back to the same place after a reset.</summary>
public readonly record struct ScrollAnchor<T>(T Item, double Offset) where T : class;

public static class ListViews
{
    public static LoadMoreTrigger LoadMoreNearEnd(Gtk.ScrolledWindow scroller, double thresholdPixels, Action loadMore)
        => new(scroller, thresholdPixels, loadMore);

    public static void ScrollTo(Gtk.ListView list, uint position)
    {
        if (list.GetModel() is Gio.ListModel model && position < model.GetNItems()) list.ScrollTo(position, Gtk.ListScrollFlags.None, null);
    }

    public static ScrollAnchor<TItem>? CaptureTop<TCell, TItem>(WinoCellFactory<TCell, TItem> factory, Gtk.ScrolledWindow scroller)
        where TCell : class, IWinoCell<TItem>
        where TItem : class
        => factory.TryGetTop(scroller, out var item, out var offset) && item is not null ? new ScrollAnchor<TItem>(item, offset) : null;

    /// <summary>Scrolls to <paramref name="position"/> (the anchor's position now), then on the next frame moves the anchor row back to its offset.</summary>
    public static void RestoreTop<TCell, TItem>(Gtk.ListView list, WinoCellFactory<TCell, TItem> factory, Gtk.ScrolledWindow scroller,
        ScrollAnchor<TItem> anchor, uint position)
        where TCell : class, IWinoCell<TItem>
        where TItem : class
    {
        ScrollTo(list, position);
        // ScrollTo lands on a later frame, and a freshly bound cell can report the bounds of its previous place; the
        // row is moved once, on the first of up to ten frames where it shows inside the scroller.
        var frames = 0;
        list.AddTickCallback((_, _) =>
        {
            if (factory.CellOf(anchor.Item) is { } cell && cell.Root.ComputeBounds(scroller, out var bounds)
                && bounds.GetY() + bounds.GetHeight() > 0 && bounds.GetY() < scroller.GetHeight())
            {
                var adjustment = scroller.GetVadjustment();
                adjustment.SetValue(adjustment.GetValue() + (bounds.GetY() - anchor.Offset));
                return false;
            }
            return ++frames < 10;
        });
    }

    public static uint[] SelectedPositions(Gtk.SelectionModel selection)
    {
        using var set = selection.GetSelection();
        var count = (uint)set.GetSize();
        var positions = new uint[count];
        for (uint index = 0; index < count; index++) positions[index] = set.GetNth(index);
        return positions;
    }

    public static T[] SelectedValues<T>(Gtk.MultiSelection selection) where T : class
    {
        var values = new List<T>();
        foreach (var position in SelectedPositions(selection))
        {
            if (ManagedItem.ValueOf<T>(selection.GetObject(position)) is { } value) values.Add(value);
        }
        return [.. values];
    }

    public static T? SelectedValue<T>(Gtk.SingleSelection selection) where T : class
        => ManagedItem.ValueOf<T>(selection.GetSelectedItem());

    /// <summary>Replaces the selection with <paramref name="positions"/>.</summary>
    public static void Select(Gtk.MultiSelection selection, uint[] positions)
    {
        var count = selection.GetNItems();
        using var selected = Gtk.Bitset.NewEmpty();
        foreach (var position in positions)
        {
            if (position < count) selected.Add(position);
        }
        using var mask = Gtk.Bitset.NewRange(0, count);
        selection.SetSelection(selected, mask);
    }

    /// <summary>
    /// Keeps the selected items selected across every splice of <paramref name="model"/>. <paramref name="positionOf"/>
    /// maps an item to its position in <paramref name="selection"/>: <c>model.IndexOf</c> without sections,
    /// <c>sections.PositionOf</c> with them.
    /// </summary>
    public static IDisposable KeepSelection<T>(ObservableListModel<T> model, Gtk.MultiSelection selection, Func<T, uint?> positionOf) where T : class
    {
        T[] kept = [];
        Action<ListChange> changing = _ => kept = SelectedValues<T>(selection);
        Action<ListChange> changed = _ =>
        {
            if (kept.Length == 0) return;
            var positions = new List<uint>(kept.Length);
            foreach (var item in kept)
            {
                if (positionOf(item) is { } position) positions.Add(position);
            }
            kept = [];
            Select(selection, [.. positions]);
        };
        model.Changing += changing;
        model.Changed += changed;
        return new ActionDisposable(() =>
        {
            model.Changing -= changing;
            model.Changed -= changed;
        });
    }
}
