using System.Diagnostics;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Collections;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Views.Mail;

internal enum MailListApplyKind { None, Append, Insert, Remove, Replace, Reset }

/// <summary>The list before an apply, for hooks that react to what the apply removed (auto-select-next).</summary>
internal struct MailListApplyContext
{
    public int PreviousRowCount { get; set; }
    public int PreviousSelectionCount { get; set; }
    public int PreviousFirstSelected { get; set; }
    public MailListEntry? SelectAfter { get; set; }
}

/// <summary>
/// Applies projection changes to the list store: at most one apply per <see cref="ProjectionThrottleMilliseconds"/>
/// (the first after a quiet period on the next main-loop turn), each as the smallest splices (append, up to eight
/// insert or remove runs, else one trimmed replace with the top row kept in place). Selection, sticky header, empty
/// state and load more follow every apply.
/// </summary>
internal sealed partial class MailListView
{
    private const int ProjectionThrottleMilliseconds = 120;
    private const int LoadingStateDelayMilliseconds = 120;
    private const int MaximumRuns = 8;

    private readonly Dictionary<string, MailListEntry> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<MailListRow, MailListEntry> _byRow = new(ReferenceEqualityComparer.Instance);
    private readonly List<(int Start, int Count)> _runs = [];
    private readonly Dictionary<nint, MailRowCell> _cellRoots = [];
    private List<MailListEntry> _shown = [];
    private List<MailListEntry> _next = [];
    private long _lastApply;
    private int _applyScheduled;
    private IDisposable? _applyTimer;
    private IDisposable? _loadingDelay;
    private string _page = "list";
    private bool _grouped = true;
    private bool _stickyShown;
    private bool _stickyPending;
    private bool _firstRowsReported;
    private bool _folderLoaded;
    private int _pass;

    /// <summary>Applies a pending projection change now instead of waiting for the throttle.</summary>
    internal void ApplyNow(bool force = false)
    {
        CancelScheduledApply();
        Apply(force);
    }

    // Any thread: the projection follows the mail collection, which a synchronization changes in batches.
    private void ProjectionChanged()
    {
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 1) return;
        RunOnUI(ScheduleApply);
    }

    private void ScheduleApply()
    {
        if (Volatile.Read(ref _applyScheduled) == 0) return;
        var since = Stopwatch.GetElapsedTime(_lastApply).TotalMilliseconds;
        var delay = since >= ProjectionThrottleMilliseconds ? 0 : ProjectionThrottleMilliseconds - since;
        var self = _self;
        _applyTimer?.Dispose();
        _applyTimer = MainLoop.Delay((uint)Math.Ceiling(delay), () => self.Value?.ApplyScheduled());
    }

    private void ApplyScheduled()
    {
        _applyTimer = null;
        Interlocked.Exchange(ref _applyScheduled, 0);
        _lastApply = Stopwatch.GetTimestamp();
        Apply(force: false);
    }

    private void CancelScheduledApply()
    {
        _applyTimer?.Dispose();
        _applyTimer = null;
        Interlocked.Exchange(ref _applyScheduled, 0);
    }

    private void UpdateGrouping()
    {
        _grouped = (ViewModel.MailListOptions?.GroupMode ?? MailListGroupMode.Date) != MailListGroupMode.None;
        List.SetHeaderFactory(_grouped ? _headers.Factory : null);
        ApplyNow(force: true);
    }

    /// <param name="force">The appearance changed: every bound cell maps its row again.</param>
    private void Apply(bool force)
    {
        if (IsReleased) return;
        var started = Stopwatch.GetTimestamp();
        var pass = ++_pass;
        _next.Clear();
        List<MailListEntry>? rebinding = null;
        var ordinalsChanged = false;
        var titlesChanged = false;
        var ordinal = -1;
        foreach (var group in Projection.Groups)
        {
            if (group.Count == 0) continue;
            ordinal++;
            // Formatting a title allocates: once per group, never per row.
            var title = _grouped ? MailRowPresentation.GroupTitle(group.Key) : string.Empty;
            var groupOrdinal = _grouped ? ordinal : 0;
            foreach (var row in group)
            {
                var entry = Resolve(row, ref rebinding, out var created);
                if (entry.Pass == pass) continue;
                entry.Pass = pass;
                if (entry.GroupOrdinal != groupOrdinal)
                {
                    ordinalsChanged |= !created;
                    entry.GroupOrdinal = groupOrdinal;
                }
                if (!string.Equals(entry.GroupTitle, title, StringComparison.Ordinal))
                {
                    titlesChanged |= !created;
                    entry.GroupTitle = title;
                }
                _next.Add(entry);
            }
        }

        var context = new MailListApplyContext
        {
            PreviousRowCount = _shown.Count,
            PreviousSelectionCount = SelectedRowCountNow(),
            PreviousFirstSelected = FirstSelectedPosition(),
        };
        OnApplying(ref context);

        var kind = MailListApplyKind.None;
        var atTop = _scroll.GetVadjustment().GetValue() <= 0.5;
        _applying = true;
        try
        {
            kind = Splice();
            // A group appeared or went away above unchanged rows: their sections change while the items stay.
            if (ordinalsChanged) _sections.Invalidate();
            (_shown, _next) = (_next, _shown);
            RestoreSelection(context.SelectAfter, _next);
            _next.Clear();
            if (_byKey.Count > _shown.Count + 256) Prune();
            ReadSelection();
        }
        finally { _applying = false; }

        PublishSelection();
        if (atTop && kind != MailListApplyKind.None) KeepAtTop();
        if (rebinding is not null)
        {
            foreach (var entry in rebinding) Cells.CellOf(entry)?.Rebind();
        }
        if (force) RefreshCells();
        else if (kind == MailListApplyKind.None) Cells.ForEachBound(static (cell, entry) => { if (entry.Row.IsThreadHead) cell.Refresh(); });
        if (titlesChanged) RetitleHeaders();
        UpdateState();
        RequestSticky();
        _loadMore.Check();
        OnApplied(kind);
        Applied?.Invoke(kind);
        MailListPerf.Applied(kind, _shown.Count, started);
        if (!_firstRowsReported && _shown.Count > 0)
        {
            _firstRowsReported = true;
            MailListPerf.FirstRows(this, _shown.Count);
        }
    }

    /// <summary>
    /// GTK keeps the first row at the top edge when rows arrive, which hides the first section header and any new row
    /// above it. A list that was at the top stays at the top, as on the other heads, once the new rows are laid out.
    /// </summary>
    private void KeepAtTop()
    {
        var self = _self;
        var frames = 0;
        List.AddTickCallback((_, _) =>
        {
            if (self.Value is not { } view) return false;
            var adjustment = view._scroll.GetVadjustment();
            if (adjustment.GetValue() > 0) adjustment.SetValue(adjustment.GetLower());
            return ++frames < 2;
        });
    }

    private MailListEntry Resolve(MailListRow row, ref List<MailListEntry>? rebinding, out bool created)
    {
        created = false;
        if (_byRow.TryGetValue(row, out var entry)) return entry;
        var key = MailListSelectionSnapshotBuilder.RowKey(row);
        if (_byKey.TryGetValue(key, out entry))
        {
            if (!ReferenceEquals(entry.Row, row))
            {
                _byRow.Remove(entry.Row);
                entry.Row = row;
                (rebinding ??= []).Add(entry);
            }
        }
        else
        {
            entry = new MailListEntry(row, key);
            _byKey[key] = entry;
            created = true;
        }
        _byRow[row] = entry;
        return entry;
    }

    private void Prune()
    {
        _byKey.Clear();
        _byRow.Clear();
        foreach (var entry in _shown)
        {
            _byKey[entry.Key] = entry;
            _byRow[entry.Row] = entry;
        }
    }

    private MailListApplyKind Splice()
    {
        var shown = _shown;
        var next = _next;
        if (shown.Count == next.Count && SamePrefix(shown, next, shown.Count)) return MailListApplyKind.None;
        if (shown.Count < next.Count && SamePrefix(shown, next, shown.Count))
        {
            _model.SetItems(next);
            return MailListApplyKind.Append;
        }

        if (shown.Count != next.Count && FindRuns(shown, next))
        {
            var inserting = next.Count > shown.Count;
            if (_runs.Count == 1) _model.SetItems(next);
            else
            {
                var working = new List<MailListEntry>(shown);
                if (inserting)
                {
                    foreach (var (start, count) in _runs)
                    {
                        working.InsertRange(start, next.GetRange(start, count));
                        _model.SetItems(working);
                    }
                }
                else
                {
                    for (var index = _runs.Count - 1; index >= 0; index--)
                    {
                        var (start, count) = _runs[index];
                        working.RemoveRange(start, count);
                        _model.SetItems(working);
                    }
                }
            }
            return inserting ? MailListApplyKind.Insert : MailListApplyKind.Remove;
        }

        var reset = shown.Count > 0 && next.Count > 0 && !ReferenceEquals(shown[0], next[0]) && !ReferenceEquals(shown[^1], next[^1]);
        var anchor = _scroll.GetVadjustment().GetValue() > 0.5 ? ListViews.CaptureTop(Cells, _scroll) : null;
        _model.SetItems(next);
        if (anchor is { } top && _model.IndexOf(top.Item) is >= 0 and var position) ListViews.RestoreTop(List, Cells, _scroll, top, (uint)position);
        return reset ? MailListApplyKind.Reset : MailListApplyKind.Replace;
    }

    private static bool SamePrefix(List<MailListEntry> a, List<MailListEntry> b, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (!ReferenceEquals(a[index], b[index])) return false;
        }
        return true;
    }

    /// <summary>A pure insert or remove: the shorter list appears in the longer one in order. Runs are in the longer list's positions.</summary>
    private bool FindRuns(List<MailListEntry> shown, List<MailListEntry> next)
    {
        var longer = next.Count > shown.Count ? next : shown;
        var shorter = next.Count > shown.Count ? shown : next;
        _runs.Clear();
        var matched = 0;
        for (var index = 0; index < longer.Count; index++)
        {
            if (matched < shorter.Count && ReferenceEquals(longer[index], shorter[matched]))
            {
                matched++;
                continue;
            }
            if (_runs.Count > 0 && _runs[^1].Start + _runs[^1].Count == index) _runs[^1] = (_runs[^1].Start, _runs[^1].Count + 1);
            else if (_runs.Count == MaximumRuns) return false;
            else _runs.Add((index, 1));
        }
        return matched == shorter.Count && _runs.Count > 0;
    }

    private int SelectedRowCountNow()
    {
        using var set = Selection.GetSelection();
        return (int)set.GetSize();
    }

    private int FirstSelectedPosition()
    {
        using var set = Selection.GetSelection();
        return set.IsEmpty() ? -1 : (int)set.GetMinimum();
    }

    /// <summary>The top row is known only after GTK laid out the new scroll position: the update waits one frame.</summary>
    private void RequestSticky()
    {
        if (_stickyPending || IsReleased) return;
        _stickyPending = true;
        var self = _self;
        var ticks = 0;
        List.AddTickCallback((_, _) =>
        {
            if (self.Value is not { } view) return false;
            if (++ticks < 2) return true;
            view._stickyPending = false;
            view.UpdateSticky();
            return false;
        });
    }

    private void UpdateSticky()
    {
        if (IsReleased) return;
        string? title = null;
        if (_grouped && _scroll.GetVadjustment().GetValue() > 0.5 && EntryAtTop() is { GroupTitle.Length: > 0 } top)
            title = top.GroupTitle;
        if (title is not null) _sticky.Title = title;
        if (_stickyShown == title is not null) return;
        _stickyShown = title is not null;
        _sticky.Root.SetVisible(_stickyShown);
    }

    /// <summary>
    /// The row (or the section header) at the top edge, by one pick and a short walk up, instead of measuring every
    /// bound cell on each scroll frame.
    /// </summary>
    private MailListEntry? EntryAtTop()
    {
        for (var widget = List.Pick(List.GetWidth() / 2.0, 1, Gtk.PickFlags.Default); widget is not null && !ReferenceEquals(widget, List); widget = widget.GetParent())
        {
            if (_cellRoots.TryGetValue(widget.Handle.DangerousGetHandle(), out var cell)) return Cells.ItemOf(cell);
            if (_headerSlots.TryGetValue(widget, out var slot)) return slot.Entry;
        }
        return Cells.TryGetTop(_scroll, out var top, out _) ? top : null;
    }

    private void UpdateState()
    {
        if (IsReleased) return;
        UpdateLoadingMore();
        if (RowCount > 0)
        {
            CancelLoadingState();
            ShowPage("list");
            return;
        }
        if (ViewModel.IsInitializingFolder || !_folderLoaded)
        {
            // A fast folder switch never flashes the spinner.
            if (_page != "loading" && _loadingDelay is null)
            {
                var self = _self;
                _loadingDelay = MainLoop.Delay(LoadingStateDelayMilliseconds, () =>
                {
                    if (self.Value is not { } view) return;
                    view._loadingDelay = null;
                    if (view.RowCount == 0 && (view.ViewModel.IsInitializingFolder || !view._folderLoaded)) view.ShowPage("loading");
                });
            }
            return;
        }
        CancelLoadingState();
        _states.Show(!ViewModel.IsInSearchMode ? MailListEmptyState.FolderEmpty
            : ViewModel.AreSearchResultsOnline && ViewModel.HasNoOnlineSearchResult ? MailListEmptyState.NoOnlineSearchResult
            : MailListEmptyState.NoSearchResult);
        ShowPage("state");
    }

    private void ShowPage(string page)
    {
        if (_page == page) return;
        _page = page;
        _stack.SetVisibleChildName(page);
    }

    private void CancelLoadingState()
    {
        _loadingDelay?.Dispose();
        _loadingDelay = null;
    }
}
