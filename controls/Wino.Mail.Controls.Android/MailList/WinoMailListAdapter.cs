using System.ComponentModel;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Wino.Mail.Controls.Core;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;

namespace Wino.Mail.Controls.Android.MailList;

/// <summary>One entry of the flattened list: a group strip or a projection row, keyed by stable identity.</summary>
public abstract record WinoMailListEntry(string Key);

public sealed record WinoMailListHeaderEntry(string Key, string Title) : WinoMailListEntry(Key);

public sealed record WinoMailListRowEntry(string Key, MailListRow Row) : WinoMailListEntry(Key);

/// <summary>
/// RecyclerView adapter over the shared <see cref="MailListProjection"/> (the twin of the iOS diffable table source):
/// the projection's groups become group strips (when <see cref="IsGrouped"/>) followed by their rows, keyed by the row's
/// stable identity (thread key for thread heads, mail id otherwise) with stable item ids. Projection changes are
/// coalesced (synchronization publishes many small batches); small changes animate through DiffUtil, large swaps (a
/// folder load, a re-sort) rebind. A bound row follows its leaves' property changes and re-maps itself, coalesced per
/// main-loop turn. Row content comes from the page's <c>map</c> delegate (shared <c>MailRowPresentation</c> rules plus
/// colours and pictures); the adapter only keeps the selection keys and the select mode, and raises row events.
/// </summary>
public sealed class WinoMailListAdapter : RecyclerView.Adapter
{
    private const int HeaderType = 0;
    private const int RowType = 1;
    private const int AnimatedChangeLimit = 40;
    private static readonly TimeSpan ProjectionThrottle = TimeSpan.FromMilliseconds(120);

    private readonly Func<MailListRow, WinoMailRowModel?> _map;
    private readonly Func<object?, string> _groupTitle;
    private readonly Handler _handler = new(Looper.MainLooper ?? throw new InvalidOperationException("The main looper is unavailable."));
    private readonly Dictionary<string, long> _ids = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedKeys = new(StringComparer.Ordinal);
    private readonly HashSet<RowHolder> _bound = new();
    private List<WinoMailListEntry> _entries = new();
    private MailListProjection? _projection;
    private long _nextId = 1;
    private bool _applyScheduled;
    private long _lastApply;
    private bool _isSelectionMode;
    private bool _isGrouped = true;

    /// <param name="map">Maps a projection row to its row model; null skips the row's content.</param>
    /// <param name="groupTitle">The title of a projection group key (shared <c>MailRowPresentation.GroupTitle</c>).</param>
    public WinoMailListAdapter(Func<MailListRow, WinoMailRowModel?> map, Func<object?, string> groupTitle)
    {
        _map = map;
        _groupTitle = groupTitle;
        HasStableIds = true;
    }

    /// <summary>A row was tapped (the page opens it or toggles it in select mode).</summary>
    public event EventHandler<MailListRow>? RowClicked;

    /// <summary>A row was long-pressed (the page starts the multi-select ActionMode).</summary>
    public event EventHandler<MailListRow>? RowLongClicked;

    /// <summary>The selection keys changed through <see cref="SetSelected"/>, <see cref="SelectOnly"/> or <see cref="ClearSelection"/>.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The entries were replaced after a projection change (empty state, paging checks).</summary>
    public event EventHandler? EntriesChanged;

    public IReadOnlyList<WinoMailListEntry> Entries => _entries;

    public MailListProjection? Projection => _projection;

    /// <summary>Shows date strips for the projection's groups; ungrouped lists show rows only.</summary>
    public bool IsGrouped
    {
        get => _isGrouped;
        set
        {
            if (_isGrouped == value) return;
            _isGrouped = value;
            Apply(animate: false);
        }
    }

    /// <summary>Rows show the select check; taps toggle selection (the page's ActionMode).</summary>
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            if (_isSelectionMode == value) return;
            _isSelectionMode = value;
            NotifyItemRangeChanged(0, _entries.Count, SelectionPayload.Instance);
        }
    }

    /// <summary>The identity of a row: the thread key for thread heads, the mail id otherwise (same as the iOS list).</summary>
    public static string RowKey(MailListRow row)
        => row.IsThreadHead ? "T:" + row.ThreadKey : "I:" + row.SourceItem.StableId.ToString("N");

    /// <summary>Shows <paramref name="projection"/>; the adapter does not own or dispose it.</summary>
    public void Attach(MailListProjection projection)
    {
        Detach();
        _projection = projection;
        projection.ProjectionChanged += ProjectionChanged;
        Apply(animate: false);
    }

    /// <summary>Stops following the projection and clears the list.</summary>
    public void Detach()
    {
        if (_projection is not null) _projection.ProjectionChanged -= ProjectionChanged;
        _projection = null;
        _applyScheduled = false;
        UnbindAll();
        _selectedKeys.Clear();
        _entries = new();
        NotifyDataSetChanged();
    }

    /// <summary>Re-maps every bound row (preferences, pictures or theme changed).</summary>
    public void RefreshRows() => NotifyItemRangeChanged(0, _entries.Count, ContentPayload.Instance);

    // ---- Selection ----

    public bool IsSelected(MailListRow row) => _selectedKeys.Contains(RowKey(row));

    public void SetSelected(MailListRow row, bool selected)
    {
        var key = RowKey(row);
        if (selected ? !_selectedKeys.Add(key) : !_selectedKeys.Remove(key)) return;
        NotifyKeyChanged(key);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects only <paramref name="row"/> (a tap outside select mode).</summary>
    public void SelectOnly(MailListRow row)
    {
        var key = RowKey(row);
        if (_selectedKeys.Count == 1 && _selectedKeys.Contains(key)) return;
        var previous = _selectedKeys.ToArray();
        _selectedKeys.Clear();
        _selectedKeys.Add(key);
        foreach (var old in previous) NotifyKeyChanged(old);
        NotifyKeyChanged(key);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll()
    {
        foreach (var entry in _entries)
            if (entry is WinoMailListRowEntry) _selectedKeys.Add(entry.Key);
        NotifyItemRangeChanged(0, _entries.Count, SelectionPayload.Instance);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection()
    {
        if (_selectedKeys.Count == 0) return;
        _selectedKeys.Clear();
        NotifyItemRangeChanged(0, _entries.Count, SelectionPayload.Instance);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The selected rows in display order (the page builds the selection snapshot from them).</summary>
    public IReadOnlyList<MailListRow> SelectedRows()
    {
        var rows = new List<MailListRow>();
        foreach (var entry in _entries)
            if (entry is WinoMailListRowEntry row && _selectedKeys.Contains(entry.Key)) rows.Add(row.Row);
        return rows;
    }

    public int PositionOf(MailListRow row)
    {
        var key = RowKey(row);
        return _entries.FindIndex(entry => entry.Key == key);
    }

    public MailListRow? RowAt(int position)
        => position >= 0 && position < _entries.Count && _entries[position] is WinoMailListRowEntry row ? row.Row : null;

    /// <summary>Expands or collapses the row's thread in the projection.</summary>
    public void ToggleThread(MailListRow row)
    {
        if (_projection is null || row.Thread is null) return;
        if (row.IsExpanded) _projection.CollapseThread(row.Thread.Key);
        else _projection.ExpandThread(row.Thread.Key);
    }

    private void NotifyKeyChanged(string key)
    {
        int index = _entries.FindIndex(entry => entry.Key == key);
        if (index >= 0) NotifyItemChanged(index, SelectionPayload.Instance);
    }

    // ---- Projection ----

    private void ProjectionChanged(object? sender, EventArgs args)
    {
        // Raised on the thread that changed the source; applied on the main thread at most every 120 ms.
        _handler.Post(() =>
        {
            if (_applyScheduled || _projection is null) return;
            _applyScheduled = true;
            var sinceLast = System.Diagnostics.Stopwatch.GetElapsedTime(_lastApply);
            long delay = sinceLast >= ProjectionThrottle ? 0 : (long)(ProjectionThrottle - sinceLast).TotalMilliseconds;
            _handler.PostDelayed(() =>
            {
                if (!_applyScheduled) return;
                _applyScheduled = false;
                _lastApply = System.Diagnostics.Stopwatch.GetTimestamp();
                Apply(animate: true);
            }, delay);
        });
    }

    private void Apply(bool animate)
    {
        var next = Flatten();
        var previous = _entries;
        _entries = next;
        // Selection follows identity; rows that left the list leave the selection.
        int selected = _selectedKeys.Count;
        _selectedKeys.IntersectWith(next.Select(static entry => entry.Key));

        if (animate && previous.Count > 0 && Math.Abs(next.Count - previous.Count) < AnimatedChangeLimit)
            DiffUtil.CalculateDiff(new EntryDiff(previous, next), false).DispatchUpdatesTo(this);
        else
            NotifyDataSetChanged();

        if (selected != _selectedKeys.Count) SelectionChanged?.Invoke(this, EventArgs.Empty);
        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<WinoMailListEntry> Flatten()
    {
        var entries = new List<WinoMailListEntry>();
        if (_projection is null) return entries;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (var group in _projection.Groups)
        {
            if (group.Count == 0) continue;
            var title = _isGrouped ? _groupTitle(group.Key) : string.Empty;
            if (!string.IsNullOrEmpty(title))
            {
                var headerKey = "G:" + title;
                if (!seen.Add(headerKey)) headerKey += "#" + index;
                seen.Add(headerKey);
                entries.Add(new WinoMailListHeaderEntry(headerKey, title));
            }
            foreach (var row in group)
            {
                var key = RowKey(row);
                if (seen.Add(key)) entries.Add(new WinoMailListRowEntry(key, row));
            }
            index++;
        }
        return entries;
    }

    // ---- RecyclerView.Adapter ----

    public override int ItemCount => _entries.Count;

    public override int GetItemViewType(int position) => _entries[position] is WinoMailListHeaderEntry ? HeaderType : RowType;

    public override long GetItemId(int position)
    {
        var key = _entries[position].Key;
        if (!_ids.TryGetValue(key, out var id))
        {
            id = _nextId++;
            _ids[key] = id;
        }
        return id;
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var context = parent.RequireContext();
        if (viewType == HeaderType) return new HeaderHolder(WinoMailGroupHeaderView.Create(context, parent));

        var view = new WinoMailRowView(context)
        {
            LayoutParameters = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        };
        var holder = new RowHolder(view);
        view.Click += (_, _) => { if (holder.Row is { } row) RowClicked?.Invoke(this, row); };
        view.LongClick += (_, args) =>
        {
            args.Handled = holder.Row is not null;
            if (holder.Row is { } row) RowLongClicked?.Invoke(this, row);
        };
        view.ThreadToggleRequested += (_, _) => { if (holder.Row is { } row) ToggleThread(row); };
        view.CheckboxToggled += (_, _) => { if (holder.Row is { } row) SetSelected(row, !IsSelected(row)); };
        return holder;
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        switch (holder, _entries[position])
        {
            case (HeaderHolder header, WinoMailListHeaderEntry entry):
                header.Title.Text = entry.Title;
                WinoMailGroupHeaderView.RefreshTheme(header.Title);
                break;
            case (RowHolder rowHolder, WinoMailListRowEntry entry):
                BindRow(rowHolder, entry.Row);
                break;
        }
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position, IList<Java.Lang.Object> payloads)
    {
        if (holder is RowHolder rowHolder && payloads.Count > 0 && payloads.All(static payload => payload is SelectionPayload))
        {
            ApplySelection(rowHolder);
            return;
        }
        if (holder is RowHolder contentHolder && payloads.Count > 0 && payloads.All(static payload => payload is ContentPayload or SelectionPayload)
            && contentHolder.Row is { } row && _entries[position] is WinoMailListRowEntry entry && ReferenceEquals(entry.Row, row))
        {
            if (_map(row) is { } model) contentHolder.View.Apply(model);
            ApplySelection(contentHolder);
            return;
        }
        OnBindViewHolder(holder, position);
    }

    public override void OnViewRecycled(Java.Lang.Object holder)
    {
        if (holder is RowHolder row) Unbind(row);
        base.OnViewRecycled(holder);
    }

    /// <summary>The list view went away (page destroyed): rows stop following their mail items.</summary>
    public override void OnDetachedFromRecyclerView(RecyclerView recyclerView)
    {
        UnbindAll();
        base.OnDetachedFromRecyclerView(recyclerView);
    }

    private void Unbind(RowHolder holder)
    {
        holder.Unbind();
        _bound.Remove(holder);
    }

    private void UnbindAll()
    {
        foreach (var holder in _bound) holder.Unbind();
        _bound.Clear();
    }

    /// <summary>Shows <paramref name="row"/> and follows its leaves' changes, coalesced per main-loop turn.</summary>
    private void BindRow(RowHolder holder, MailListRow row)
    {
        Unbind(holder);
        _bound.Add(holder);
        holder.Row = row;
        holder.View.Item = row;
        if (_map(row) is { } initial) holder.View.Apply(initial);
        ApplySelection(holder);

        var leaves = (row.IsThreadHead ? row.LeafItems : new[] { row.SourceItem }).OfType<INotifyPropertyChanged>().ToArray();
        bool pending = false;
        bool active = true;
        // Leaves change on any thread; the flag is only touched on the main thread.
        void Schedule() => _handler.Post(() =>
        {
            if (pending) return;
            pending = true;
            _handler.Post(() =>
            {
                pending = false;
                if (!active || !ReferenceEquals(holder.Row, row)) return;
                if (_map(row) is { } model) holder.View.Apply(model);
            });
        });
        PropertyChangedEventHandler handler = (_, args) =>
        {
            // Selection is the adapter's; the ViewModel's IsSelected echo needs no re-map.
            if (args.PropertyName is "IsSelected" or "ThumbnailUpdatedEvent") return;
            Schedule();
        };
        foreach (var leaf in leaves) leaf.PropertyChanged += handler;
        PropertyChangedEventHandler rowHandler = (_, _) => Schedule();
        row.PropertyChanged += rowHandler;
        holder.Subscription = new ActionDisposable(() =>
        {
            active = false;
            foreach (var leaf in leaves) leaf.PropertyChanged -= handler;
            row.PropertyChanged -= rowHandler;
        });
    }

    private void ApplySelection(RowHolder holder)
    {
        bool selected = holder.Row is { } row && IsSelected(row);
        holder.View.IsSelectionMode = _isSelectionMode;
        holder.View.IsChecked = _isSelectionMode && selected;
        holder.View.IsRowSelected = !_isSelectionMode && selected;
    }

    private sealed class HeaderHolder(TextView title) : RecyclerView.ViewHolder(title)
    {
        public TextView Title { get; } = title;
    }

    private sealed class RowHolder(WinoMailRowView view) : RecyclerView.ViewHolder(view)
    {
        public WinoMailRowView View { get; } = view;
        public MailListRow? Row { get; set; }
        public IDisposable? Subscription { get; set; }

        public void Unbind()
        {
            Subscription?.Dispose();
            Subscription = null;
            Row = null;
            View.Item = null;
        }
    }

    /// <summary>Partial rebind: only the selection look changed.</summary>
    private sealed class SelectionPayload : Java.Lang.Object
    {
        public static readonly SelectionPayload Instance = new();
    }

    /// <summary>Partial rebind: re-map the same row (preferences, pictures, accent).</summary>
    private sealed class ContentPayload : Java.Lang.Object
    {
        public static readonly ContentPayload Instance = new();
    }

    /// <summary>Same key is the same item; same entry (same row object, same title) is the same content.</summary>
    private sealed class EntryDiff(List<WinoMailListEntry> previous, List<WinoMailListEntry> next) : DiffUtil.Callback
    {
        public override int OldListSize => previous.Count;
        public override int NewListSize => next.Count;
        public override bool AreItemsTheSame(int oldItemPosition, int newItemPosition) => previous[oldItemPosition].Key == next[newItemPosition].Key;
        public override bool AreContentsTheSame(int oldItemPosition, int newItemPosition) => previous[oldItemPosition] == next[newItemPosition];
    }
}
