using System.Collections.Specialized;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>Lists on one page that can exchange rows (Mac SettingsDragGroup).</summary>
internal sealed class SettingsDragGroup
{
    private readonly List<ISettingsDragList> _lists = [];

    /// <summary>Whether a row of the first list may land in the second one. A list reorders itself when it may drop on itself.</summary>
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
/// A non-scrolling table of custom row views sized to its rows, for settings pages (Mac SettingsDragList). The table
/// stays in edit mode, so a list that may drop on itself shows the system reorder handles and a drag reorders the row
/// (VoiceOver offers the same move through the handle's actions). Moving a row to another list of the
/// <see cref="SettingsDragGroup"/> goes through the row's own buttons on iOS (Pin, Move to More, Link, Unlink).
/// An optional empty view (shown with no rows) and footer view sit behind the table at its bottom edge.
/// </summary>
internal sealed class SettingsDragList<T> : UIView, ISettingsDragList where T : class
{
    private readonly UITableView _table;
    private readonly NSLayoutConstraint _height;
    private readonly Source _source;
    private readonly IList<T> _items;
    private readonly SettingsDragGroup _group;
    private readonly double _rowHeight;
    private readonly double _rowSpacing;
    private UIView? _emptyView;
    private UIView? _footerView;
    private double _emptyHeight = 44;
    private double _footerHeight;

    public SettingsDragList(string key, IList<T> items, SettingsDragGroup group, Func<T, int, UIView> makeRow, double rowHeight, double rowSpacing = 0)
    {
        Key = key;
        _items = items;
        _group = group;
        _rowHeight = rowHeight;
        _rowSpacing = rowSpacing;
        TranslatesAutoresizingMaskIntoConstraints = false;

        _source = new Source(this, makeRow);
        _table = new UITableView(CGRect.Empty, UITableViewStyle.Plain)
        {
            RowHeight = (nfloat)(rowHeight + rowSpacing),
            EstimatedRowHeight = (nfloat)(rowHeight + rowSpacing),
            SeparatorStyle = UITableViewCellSeparatorStyle.None,
            BackgroundColor = UIColor.Clear,
            ScrollEnabled = false,
            AllowsSelection = false,
            AllowsSelectionDuringEditing = false,
            SectionHeaderTopPadding = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _table.RegisterClassForCellReuse(typeof(SettingsDragListHostCell), SettingsDragListHostCell.Identifier);
        _table.Source = _source;
        _table.SetEditing(group.CanDrop(this, this), false);
        WinoLayout.Fill(_table, this);
        _height = HeightAnchor.ConstraintEqualTo(1);
        _height.Active = true;
        group.Add(this);
        Reload();
    }

    public string Key { get; }
    public int Count => _items.Count;
    public IList<T> Items => _items;
    public SettingsDragGroup Group => _group;
    public UITableView Table => _table;

    /// <summary>Whether rows show reorder handles (the group lets this list drop on itself).</summary>
    public bool CanReorder => _group.CanDrop(this, this);

    /// <summary>Shown behind the table while the list is empty (a dashed placeholder).</summary>
    public void SetEmptyView(UIView view, double height)
    {
        _emptyView?.RemoveFromSuperview();
        _emptyView = view;
        _emptyHeight = height;
        AddBehind(view, height);
        Reload();
    }

    /// <summary>Always shown under the rows.</summary>
    public void SetFooterView(UIView view, double height)
    {
        _footerView?.RemoveFromSuperview();
        _footerView = view;
        _footerHeight = height;
        AddBehind(view, height);
        Reload();
    }

    private void AddBehind(UIView view, double height)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        InsertSubviewBelow(view, _table);
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
        _table.SetEditing(CanReorder, false);
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
        _table.ReloadRows([NSIndexPath.FromRowSection(index, 0)], UITableViewRowAnimation.None);
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
            _table.Source = null;
            _source.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Moved(int from, int to)
    {
        if (from == to || from < 0 || from >= _items.Count) return;
        // The table already shows the new order; the page moves the item, and the collection change reloads the rows.
        BeginInvokeOnMainThread(() => _group.Dropped?.Invoke(this, from, this, Math.Clamp(to, 0, Math.Max(0, _items.Count - 1))));
    }

    private sealed class Source(SettingsDragList<T> owner, Func<T, int, UIView> makeRow) : UITableViewSource
    {
        public override nint RowsInSection(UITableView tableview, nint section) => owner._items.Count;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = (SettingsDragListHostCell)tableView.DequeueReusableCell(SettingsDragListHostCell.Identifier, indexPath);
            var index = indexPath.Row;
            var view = index >= 0 && index < owner._items.Count ? makeRow(owner._items[index], index) : new UIView();
            cell.Host(view, owner._rowSpacing);
            return cell;
        }

        public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath) => owner.CanReorder;

        public override bool CanMoveRow(UITableView tableView, NSIndexPath indexPath) => owner.CanReorder;

        public override UITableViewCellEditingStyle EditingStyleForRow(UITableView tableView, NSIndexPath indexPath) => UITableViewCellEditingStyle.None;

        public override bool ShouldIndentWhileEditing(UITableView tableView, NSIndexPath indexPath) => false;

        public override void MoveRow(UITableView tableView, NSIndexPath sourceIndexPath, NSIndexPath destinationIndexPath)
            => owner.Moved(sourceIndexPath.Row, destinationIndexPath.Row);
    }
}

/// <summary>
/// A clear, unselectable cell that hosts one custom row view with the row spacing under it. It is a top-level class:
/// a cell nested in the generic list cannot be constructed from Objective-C (the table's dequeue crashed the page).
/// </summary>
internal sealed class SettingsDragListHostCell : UITableViewCell
{
    public const string Identifier = "SettingsDragListRow";
    private UIView? _row;

    public SettingsDragListHostCell(ObjCRuntime.NativeHandle handle) : base(handle) => Configure();

    [Export("initWithStyle:reuseIdentifier:")]
    public SettingsDragListHostCell(UITableViewCellStyle style, string? reuseIdentifier) : base(style, reuseIdentifier) => Configure();

    private void Configure()
    {
        BackgroundColor = UIColor.Clear;
        ContentView.BackgroundColor = UIColor.Clear;
        SelectionStyle = UITableViewCellSelectionStyle.None;
        ShowsReorderControl = true;
    }

    public void Host(UIView row, double spacing)
    {
        _row?.RemoveFromSuperview();
        _row = row;
        WinoLayout.Fill(row, ContentView, 0, 0, spacing, 0);
    }

    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        _row?.RemoveFromSuperview();
        _row = null;
    }
}

/// <summary>Rounded dashed outline with a centred caption, the placeholder of an empty list (Mac DashedDropZoneView).</summary>
internal sealed class DashedDropZoneView : UIView
{
    private readonly UILabel _label;
    private readonly CAShapeLayer _border = new() { LineWidth = 1, FillColor = UIColor.Clear.CGColor, LineDashPattern = [4, 3] };

    public DashedDropZoneView(string? text)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Layer.AddSublayer(_border);
        _label = WinoStyle.Label(text, WinoStyle.Caption, WinoStyle.TertiaryText, 2);
        _label.TextAlignment = UITextAlignment.Center;
        _label.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(_label);
        NSLayoutConstraint.ActivateConstraints(
        [
            _label.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _label.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _label.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 12),
            _label.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -12)
        ]);
        ApplyStroke();
        WinoTraits.OnAppearanceChanged(this, static view => view.ApplyStroke());
    }

    public string? Text
    {
        get => _label.Text;
        set => _label.Text = value ?? string.Empty;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _border.Frame = Bounds;
        _border.Path = UIBezierPath.FromRoundedRect(Bounds.Inset(0.5f, 0.5f), 6).CGPath;
    }

    private void ApplyStroke()
        => _border.StrokeColor = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.18), WinoStyle.Hex(0xFFFFFF, 0.2)).GetResolvedColor(TraitCollection).CGColor;
}
