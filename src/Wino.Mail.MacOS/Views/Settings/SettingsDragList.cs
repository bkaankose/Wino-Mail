using System.Collections.Specialized;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>Lists on one page that can exchange rows by drag and drop.</summary>
internal sealed class SettingsDragGroup
{
    public const string DragType = "com.winomail.settings-row";
    private readonly List<ISettingsDragList> _lists = [];

    /// <summary>Identifies this page's drags, so a row dragged from another window is refused.</summary>
    public string Token { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Whether a row of the first list may land in the second one.</summary>
    public Func<ISettingsDragList, ISettingsDragList, bool> CanDrop { get; set; } = (source, target) => ReferenceEquals(source, target);

    /// <summary>Source list, source index, target list, final index in the target.</summary>
    public Action<ISettingsDragList, int, ISettingsDragList, int>? Dropped { get; set; }

    internal void Add(ISettingsDragList list) => _lists.Add(list);

    internal ISettingsDragList? Find(string key) => _lists.FirstOrDefault(list => list.Key == key);
}

internal interface ISettingsDragList
{
    string Key { get; }
    int Count { get; }
}

/// <summary>
/// A non-scrolling table of custom row views sized to its rows, for settings pages: drag a row to
/// reorder it or to move it to another list of the same <see cref="SettingsDragGroup"/>. Wheel events
/// pass to the page scroll view. An optional empty view (shown with no rows) and footer view sit behind
/// the table, so drops on them land at the end of the list.
/// </summary>
internal sealed class SettingsDragList<T> : NSView, ISettingsDragList where T : class
{
    private readonly NSTableView _table;
    private readonly PassThroughScrollView _scroll;
    private readonly NSLayoutConstraint _height;
    private readonly Source _source;
    private readonly IList<T> _items;
    private readonly SettingsDragGroup _group;
    private readonly double _rowHeight;
    private readonly double _rowSpacing;
    private NSView? _emptyView;
    private NSView? _footerView;
    private double _emptyHeight = 44;
    private double _footerHeight;

    public SettingsDragList(string key, IList<T> items, SettingsDragGroup group, Func<T, int, NSView> makeRow, double rowHeight, double rowSpacing = 0)
    {
        Key = key;
        _items = items;
        _group = group;
        _rowHeight = rowHeight;
        _rowSpacing = rowSpacing;
        TranslatesAutoresizingMaskIntoConstraints = false;

        _source = new Source(this, makeRow);
        _table = new NSTableView
        {
            HeaderView = null,
            RowHeight = (nfloat)rowHeight,
            IntercellSpacing = new CGSize(0, rowSpacing),
            Style = NSTableViewStyle.Plain,
            SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.None,
            BackgroundColor = NSColor.Clear,
            GridStyleMask = NSTableViewGridStyle.None,
            FocusRingType = NSFocusRingType.None,
            UsesAutomaticRowHeights = false,
            ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly,
            DraggingDestinationFeedbackStyle = NSTableViewDraggingDestinationFeedbackStyle.Regular
        };
        _table.AddColumn(new NSTableColumn("row") { ResizingMask = NSTableColumnResizing.Autoresizing });
        _table.DataSource = _source;
        _table.Delegate = _source;
        _table.RegisterForDraggedTypes([SettingsDragGroup.DragType]);
        _table.SetDraggingSourceOperationMask(NSDragOperation.Move, true);

        _scroll = new PassThroughScrollView
        {
            DocumentView = _table,
            HasVerticalScroller = false,
            HasHorizontalScroller = false,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            AutomaticallyAdjustsContentInsets = false,
            VerticalScrollElasticity = NSScrollElasticity.None,
            HorizontalScrollElasticity = NSScrollElasticity.None,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _scroll.ContentView.DrawsBackground = false;
        WinoLayout.Fill(_scroll, this);
        _height = HeightAnchor.ConstraintEqualTo(0);
        _height.Active = true;
        group.Add(this);
        Reload();
    }

    public string Key { get; }
    public int Count => _items.Count;
    public IList<T> Items => _items;
    public SettingsDragGroup Group => _group;
    public NSTableView Table => _table;

    /// <summary>Shown behind the table while the list is empty (a dashed drop zone).</summary>
    public void SetEmptyView(NSView view, double height)
    {
        _emptyView?.RemoveFromSuperview();
        _emptyView = view;
        _emptyHeight = height;
        AddBehind(view, height);
        Reload();
    }

    /// <summary>Always shown under the rows (a drop zone that ends the list).</summary>
    public void SetFooterView(NSView view, double height)
    {
        _footerView?.RemoveFromSuperview();
        _footerView = view;
        _footerHeight = height;
        AddBehind(view, height);
        Reload();
    }

    private void AddBehind(NSView view, double height)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(view, NSWindowOrderingMode.Below, _scroll);
        NSLayoutConstraint.ActivateConstraints(
        [
            view.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            view.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            view.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            view.HeightAnchor.ConstraintEqualTo((nfloat)height)
        ]);
    }

    /// <summary>Rebuilds the rows and the height from the items.</summary>
    public void Reload()
    {
        _table.ReloadData();
        var count = _items.Count;
        var rows = count * (_rowHeight + _rowSpacing);
        var height = rows;
        if (count == 0 && _emptyView is not null) height = _emptyHeight;
        if (_footerView is not null) height = rows + (count > 0 ? 6 : 0) + _footerHeight;
        _height.Constant = (nfloat)Math.Max(height, 1);
        if (_emptyView is not null) _emptyView.Hidden = count > 0;
    }

    /// <summary>Reloads one row in place, for a row whose own state changed.</summary>
    public void ReloadRow(T item)
    {
        var index = _items.IndexOf(item);
        if (index < 0) return;
        _table.ReloadData(NSIndexSet.FromIndex(index), NSIndexSet.FromIndex(0));
    }

    /// <summary>Reloads now and after each change of the observable item collection.</summary>
    public IDisposable Observe(INotifyCollectionChanged collection, IDispatcher dispatcher, Action? changed = null)
    {
        var disposed = false;
        NotifyCollectionChangedEventHandler handler = (_, _) => _ = dispatcher.ExecuteOnUIThread(() =>
        {
            if (disposed) return;
            Reload();
            changed?.Invoke();
        });
        collection.CollectionChanged += handler;
        Reload();
        changed?.Invoke();
        return new ActionDisposable(() => { disposed = true; collection.CollectionChanged -= handler; });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _table.DataSource = null;
            _table.Delegate = null;
            _source.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class Source(SettingsDragList<T> owner, Func<T, int, NSView> makeRow) : NSTableViewDataSource, INSTableViewDelegate
    {
        public override nint GetRowCount(NSTableView tableView) => owner._items.Count;

        [Export("tableView:viewForTableColumn:row:")]
        public NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var index = (int)row;
            var view = index >= 0 && index < owner._items.Count ? makeRow(owner._items[index], index) : new NSView();
            // The table sizes cell views by frame; their contents use Auto Layout inside.
            view.TranslatesAutoresizingMaskIntoConstraints = true;
            view.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
            return view;
        }

        [Export("tableView:shouldSelectRow:")]
        public bool ShouldSelectRow(NSTableView tableView, nint row) => false;

        public override INSPasteboardWriting GetPasteboardWriterForRow(NSTableView tableView, nint row)
        {
            var item = new NSPasteboardItem();
            item.SetStringForType($"{owner._group.Token}|{owner.Key}|{row}", SettingsDragGroup.DragType);
            return item;
        }

        public override NSDragOperation ValidateDrop(NSTableView tableView, INSDraggingInfo info, nint row, NSTableViewDropOperation dropOperation)
        {
            if (!TryRead(info, out var source, out _) || !owner._group.CanDrop(source, owner)) return NSDragOperation.None;
            if (dropOperation == NSTableViewDropOperation.On) tableView.SetDropRowDropOperation(row, NSTableViewDropOperation.Above);
            return NSDragOperation.Move;
        }

        public override bool AcceptDrop(NSTableView tableView, INSDraggingInfo info, nint row, NSTableViewDropOperation dropOperation)
        {
            if (!TryRead(info, out var source, out var from) || !owner._group.CanDrop(source, owner)) return false;
            if (from < 0 || from >= source.Count) return false;
            var target = Math.Clamp((int)row, 0, owner.Count);
            if (ReferenceEquals(source, owner))
            {
                if (target > from) target--;
                if (target == from) return false;
            }
            owner._group.Dropped?.Invoke(source, from, owner, target);
            return true;
        }

        private bool TryRead(INSDraggingInfo info, out ISettingsDragList source, out int index)
        {
            source = null!;
            index = -1;
            var value = info.DraggingPasteboard.GetStringForType(SettingsDragGroup.DragType);
            var parts = value?.Split('|');
            if (parts is not { Length: 3 } || parts[0] != owner._group.Token) return false;
            if (owner._group.Find(parts[1]) is not { } list || !int.TryParse(parts[2], out index)) return false;
            source = list;
            return true;
        }
    }

    /// <summary>The page scroll view keeps wheel scrolling while the pointer is over the list.</summary>
    private sealed class PassThroughScrollView : NSScrollView
    {
        public override void ScrollWheel(NSEvent theEvent) => NextResponder?.ScrollWheel(theEvent);
    }
}

/// <summary>Rounded dashed outline with a centred caption, the drop target of an empty list.</summary>
internal sealed class DashedDropZoneView : NSView
{
    private readonly NSTextField _label;

    public DashedDropZoneView(string? text)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _label = WinoStyle.Label(text, WinoStyle.Caption, WinoStyle.TertiaryText, 2);
        _label.Alignment = NSTextAlignment.Center;
        AddSubview(_label);
        NSLayoutConstraint.ActivateConstraints(
        [
            _label.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _label.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _label.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 12),
            _label.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -12)
        ]);
    }

    public string? Text
    {
        get => _label.StringValue;
        set => _label.StringValue = value ?? string.Empty;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        var path = NSBezierPath.FromRoundedRect(Bounds.Inset(0.5f, 0.5f), 6, 6);
        path.SetLineDash([4, 3], 0);
        path.LineWidth = 1;
        WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.18), WinoStyle.Hex(0xFFFFFF, 0.2)).SetStroke();
        path.Stroke();
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }
}
