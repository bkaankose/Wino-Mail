using System.Collections.Specialized;
using System.ComponentModel;
using Android.OS;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Android.Shell;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using IMenuItem = Wino.Core.Domain.Interfaces.IMenuItem;

namespace Wino.Mail.Android.Views.Shell;

/// <summary>
/// The shell menu as Wino shell rows in a RecyclerView (the drawer, the pinned pane; canvas boards Mailboxes and
/// Tablet): New mail, accounts with their unread pill and sync bar, linked accounts, section headers, folders by depth
/// and the footer rows. Row content comes from the shared <see cref="ShellMenuRowPresentation"/> (the Windows pane
/// templates). It follows every shown item's property changes and the menu collections, coalesced into one rebuild per
/// main-loop turn, and draws the shell's selected item with the Windows selection fill and pipe.
/// </summary>
public sealed class ShellMenuAdapter : RecyclerView.Adapter
{
    private readonly IPictureStorageService? _pictures;
    private readonly Handler _main = new(Looper.MainLooper ?? throw new InvalidOperationException("The main looper is unavailable."));
    private readonly List<(IMenuItem Item, int Depth)> _rows = [];
    private readonly List<INotifyPropertyChanged> _observedItems = [];
    private readonly List<INotifyCollectionChanged> _observedCollections = [];
    private ShellMenu? _menu;
    private object? _selected;
    private bool _rebuildScheduled;
    private bool _detached;

    public ShellMenuAdapter(IPictureStorageService? pictures)
    {
        _pictures = pictures;
        HasStableIds = false;
    }

    /// <summary>A row was tapped.</summary>
    public event EventHandler<IMenuItem>? Invoked;

    /// <summary>A row was long-pressed; the owner shows its menu anchored to the view.</summary>
    public event EventHandler<(IMenuItem Item, View Anchor)>? LongPressed;

    public IReadOnlyList<(IMenuItem Item, int Depth)> Rows => _rows;

    public void SetMenu(ShellMenu? menu)
    {
        _menu = menu;
        Rebuild();
    }

    public void SetSelected(object? selected)
    {
        if (ReferenceEquals(_selected, selected)) return;
        _selected = selected;
        NotifyDataSetChanged();
    }

    /// <summary>Stops following the menu (the view went away).</summary>
    public void Detach()
    {
        _detached = true;
        Unobserve();
        _rows.Clear();
        Invoked = null;
        LongPressed = null;
    }

    private void ScheduleRebuild()
    {
        // Menu items change on any thread; the flag is only touched on the main thread.
        _main.Post(() =>
        {
            if (_rebuildScheduled || _detached) return;
            _rebuildScheduled = true;
            _main.Post(() =>
            {
                _rebuildScheduled = false;
                if (!_detached) Rebuild();
            });
        });
    }

    private void Rebuild()
    {
        Unobserve();
        _rows.Clear();
        if (_menu is { } menu)
        {
            Observe(menu.Items);
            _rows.AddRange(ShellMenuRowPresentation.Flatten(menu.Items));
            if (menu.FooterItems is { } footer)
            {
                Observe(footer);
                _rows.AddRange(ShellMenuRowPresentation.Flatten(footer));
            }
            foreach (var (item, _) in _rows)
            {
                if (item is INotifyPropertyChanged notifying)
                {
                    notifying.PropertyChanged += ItemChanged;
                    _observedItems.Add(notifying);
                }
                Observe(ShellMenuItemRules.Children(item));
            }
        }
        NotifyDataSetChanged();
    }

    private void Observe(IEnumerable<IMenuItem> collection)
    {
        if (collection is not INotifyCollectionChanged notifying) return;
        notifying.CollectionChanged += CollectionChanged;
        _observedCollections.Add(notifying);
    }

    private void Unobserve()
    {
        foreach (var item in _observedItems) item.PropertyChanged -= ItemChanged;
        foreach (var collection in _observedCollections) collection.CollectionChanged -= CollectionChanged;
        _observedItems.Clear();
        _observedCollections.Clear();
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs args) => ScheduleRebuild();

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => ScheduleRebuild();

    public override int ItemCount => _rows.Count;

    // Calendar mode: the date picker and calendar check rows (Views/Calendar/CalendarPaneRows); 0 is the shell row.
    public override int GetItemViewType(int position)
        => position < _rows.Count ? Calendar.CalendarPaneRows.ViewTypeOf(_rows[position].Item) : 0;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        if (viewType != 0)
        {
            var paneRow = Calendar.CalendarPaneRows.Create(parent.RequireContext(), viewType);
            paneRow.LayoutParameters = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            var paneHolder = new PaneHolder(paneRow);
            if (viewType == Calendar.CalendarPaneRows.CalendarType) paneRow.Click += (_, _) => { if (paneHolder.Item is { } item) Invoked?.Invoke(this, item); };
            return paneHolder;
        }
        var view = new WinoShellRowView(parent.RequireContext())
        {
            LayoutParameters = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        };
        var holder = new RowHolder(view);
        view.Click += (_, _) => { if (view.Item is IMenuItem item) Invoked?.Invoke(this, item); };
        view.LongClick += (_, args) =>
        {
            args.Handled = view.Item is IMenuItem;
            if (view.Item is IMenuItem item) LongPressed?.Invoke(this, (item, view));
        };
        view.DisclosureToggled += (_, _) =>
        {
            if (view.Item is IMenuItem item && ShellMenuRowPresentation.HasChildren(item) && item is not IAccountMenuItem) item.IsExpanded = !item.IsExpanded;
        };
        return holder;
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is PaneHolder paneHolder && position < _rows.Count)
        {
            paneHolder.Item = _rows[position].Item;
            Calendar.CalendarPaneRows.Bind(paneHolder.ItemView, paneHolder.Item);
            return;
        }
        if (holder is not RowHolder rowHolder || position >= _rows.Count) return;
        var (item, depth) = _rows[position];
        var view = rowHolder.View;
        view.Item = item;
        var row = ShellMenuRowPresentation.Describe(item, depth, _pictures);
        switch (row.Kind)
        {
            case ShellMenuRowKind.NewItem:
                view.ConfigureNewItem(row.Title, row.Glyph);
                break;
            case ShellMenuRowKind.Header:
                view.ConfigureHeader(row.Title);
                break;
            case ShellMenuRowKind.Account when row.AccountIcon is { } icon:
                view.ConfigureAccount(icon, row.Title, row.Subtitle, row.UnreadCount);
                view.SetSync(row.ShowsSync, row.IsSyncIndeterminate, row.SyncValue);
                break;
            default:
                view.ConfigureFolder(row.Glyph, row.Title, row.UnreadCount, row.Indent, row.IsExpanded);
                break;
        }
        view.IsRowSelected = row.IsHighlightable && ReferenceEquals(item, _selected);
        view.ShowsIndicator = row.ShowsIndicator;
        view.Enabled = row.IsEnabled && ShellMenuItemRules.IsInteractive(item);
        view.Alpha = row.IsEnabled ? 1f : 0.45f;
    }

    private sealed class PaneHolder(View view) : RecyclerView.ViewHolder(view)
    {
        public IMenuItem? Item { get; set; }
    }

    private sealed class RowHolder(WinoShellRowView view) : RecyclerView.ViewHolder(view)
    {
        public WinoShellRowView View { get; } = view;
    }
}
