using AppKit;
using Foundation;
using System.Collections.Specialized;
using System.ComponentModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Controls.AppKit;

/// <summary>Reusable native list with stable identity, targeted row refresh and view-owned subscriptions.</summary>
public sealed class WinoCollectionView<T> : NSScrollView where T : class
{
    private readonly NSTableView _table = new();
    private readonly Rows _rows;
    private readonly Func<T, string> _identity;
    private readonly Func<T, string> _title;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private IList<T>? _items;
    private IList<T>? _displayItems;
    private INotifyCollectionChanged? _observable;
    private readonly List<INotifyPropertyChanged> _observedItems = new();
    private bool _released;
    private int _generation;
    private string? _selectedIdentity;
    public event EventHandler<T?>? SelectionChanged;
    public T? SelectedItem => _displayItems is not null && _table.SelectedRow >= 0 && _table.SelectedRow < _displayItems.Count ? _displayItems[(int)_table.SelectedRow] : null;
    public WinoCollectionView(Func<T, string> identity, Func<T, string> title, IDispatcher dispatcher, Action<Exception> error)
    {
        _identity = identity; _title = title; _dispatcher = dispatcher; _error = error;
        _rows = new(this);
        _table.AddColumn(new NSTableColumn("item") { Width = 240 });
        _table.HeaderView = null;
        _table.Source = _rows;
        _table.AllowsEmptySelection = true;
        _table.RowHeight = 30;
        DocumentView = _table;
        HasVerticalScroller = true;
        TranslatesAutoresizingMaskIntoConstraints = false;
    }
    public void Bind(IList<T> items)
    {
        ObjectDisposedException.ThrowIf(_released, this);
        Unbind();
        _items = items;
        _displayItems = items.ToArray();
        _observable = items as INotifyCollectionChanged;
        if (_observable is not null) _observable.CollectionChanged += CollectionChanged;
        ObserveItems();
        _table.ReloadData();
    }
    private void ObserveItems()
    {
        foreach (var item in _observedItems) item.PropertyChanged -= ItemChanged;
        _observedItems.Clear();
        if (_items is null) return;
        foreach (var item in _items.OfType<INotifyPropertyChanged>())
        {
            item.PropertyChanged += ItemChanged;
            _observedItems.Add(item);
        }
    }
    private async void ItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        int generation = _generation;
        try
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (_released || generation != _generation || _displayItems is null || sender is not T item) return;
                int index = _displayItems.IndexOf(item);
                if (index >= 0) _table.ReloadData(new NSIndexSet(index), new NSIndexSet(0));
            });
        }
        catch (Exception error) { if (!_released) _error(error); }
    }
    private async void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        int generation = _generation;
        T[] snapshot = _items?.ToArray() ?? Array.Empty<T>();
        try
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (_released || generation != _generation) return;
                string? selected = _selectedIdentity;
                ObserveItems();
                _displayItems = snapshot;
                bool incremental = args.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace
                    || (args.Action == NotifyCollectionChangedAction.Move && args.OldItems?.Count == 1);
                if (incremental) _table.BeginUpdates();
                try
                {
                    if (args.Action == NotifyCollectionChangedAction.Add && args.NewStartingIndex >= 0)
                        _table.InsertRows(NSIndexSet.FromArray(Enumerable.Range(args.NewStartingIndex, args.NewItems!.Count).ToArray()), NSTableViewAnimation.None);
                    else if (args.Action == NotifyCollectionChangedAction.Remove && args.OldStartingIndex >= 0)
                        _table.RemoveRows(NSIndexSet.FromArray(Enumerable.Range(args.OldStartingIndex, args.OldItems!.Count).ToArray()), NSTableViewAnimation.None);
                    else if (args.Action == NotifyCollectionChangedAction.Replace && args.NewStartingIndex >= 0)
                        _table.ReloadData(NSIndexSet.FromArray(Enumerable.Range(args.NewStartingIndex, args.NewItems!.Count).ToArray()), new NSIndexSet(0));
                    else if (args.Action == NotifyCollectionChangedAction.Move && args.OldItems?.Count == 1)
                        _table.MoveRow(args.OldStartingIndex, args.NewStartingIndex);
                    else _table.ReloadData();
                }
                finally { if (incremental) _table.EndUpdates(); }
                if (selected is not null && _displayItems is not null)
                {
                    int index = Enumerable.Range(0, _displayItems.Count).FirstOrDefault(index => _identity(_displayItems[index]) == selected, -1);
                    if (index >= 0) _table.SelectRows(new NSIndexSet(index), false);
                    else _table.DeselectAll(null);
                }
            });
        }
        catch (Exception error) { if (!_released) _error(error); }
    }
    private void Unbind()
    {
        _generation++;
        if (_observable is not null) _observable.CollectionChanged -= CollectionChanged;
        foreach (var item in _observedItems) item.PropertyChanged -= ItemChanged;
        _observedItems.Clear(); _observable = null; _items = null; _displayItems = null; _selectedIdentity = null;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_released)
        {
            _released = true;
            Unbind();
            SelectionChanged = null;
            _table.Source = null;
            _rows.Dispose(); _table.Dispose();
        }
        base.Dispose(disposing);
    }
    private sealed class Rows(WinoCollectionView<T> owner) : NSTableViewSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner._displayItems?.Count ?? 0;
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var label = tableView.MakeView("row", this) as NSTextField ?? new NSTextField {
                Identifier = "row", Editable = false, Selectable = false, Bordered = false, DrawsBackground = false };
            label.StringValue = owner._title(owner._displayItems![(int)row]);
            return label;
        }
        public override void SelectionDidChange(NSNotification notification)
        {
            owner._selectedIdentity = owner.SelectedItem is { } item ? owner._identity(item) : null;
            owner.SelectionChanged?.Invoke(owner, owner.SelectedItem);
        }
    }
}
