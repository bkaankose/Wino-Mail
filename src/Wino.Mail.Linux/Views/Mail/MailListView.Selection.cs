using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Messaging.Client.Mails;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// Selection, select mode, conversations and list keys. The selection is read from the view's own row array (store
/// order is screen order), never item by item from GTK; select all is one range bitset. Several selected messages
/// cover the reading pane with <see cref="MailMultiSelectionView"/>.
/// </summary>
internal sealed partial class MailListView
{
    private const int WatchedSelectionLimit = 200;

    private static readonly uint KeyEscape = Gdk.Functions.KeyvalFromName("Escape");
    private static readonly uint KeyDelete = Gdk.Functions.KeyvalFromName("Delete");
    private static readonly uint KeyPadDelete = Gdk.Functions.KeyvalFromName("KP_Delete");
    private static readonly uint KeyA = Gdk.Functions.KeyvalFromName("a");
    private static readonly uint KeyZ = Gdk.Functions.KeyvalFromName("z");
    private static readonly uint KeyRight = Gdk.Functions.KeyvalFromName("Right");
    private static readonly uint KeyPadRight = Gdk.Functions.KeyvalFromName("KP_Right");
    private static readonly uint KeyLeft = Gdk.Functions.KeyvalFromName("Left");
    private static readonly uint KeyPadLeft = Gdk.Functions.KeyvalFromName("KP_Left");

    private readonly HashSet<MailListEntry> _selected = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<MailRowCell, IDisposable> _cellSelection = new();
    private readonly List<INotifyPropertyChanged> _watchedSelection = [];
    private PropertyChangedEventHandler? _selectedItemChanged;
    private MainLoop.Coalescer? _multiRefresh;
    private MailMultiSelectionView? _multi;
    private MoveFolderPopover? _movePopover;
    private string _published = string.Empty;
    private bool _applying;
    private bool _allSelected;
    private bool _selectionMode;
    private bool _coverShown;
    private bool _deleteHeld;
    private uint _lastChangedPosition = uint.MaxValue;
    private MailListEntry? _active;

    internal bool IsSelectionMode => _selectionMode;

    /// <summary>Selected rows (a thread head counts once), not mails.</summary>
    internal int SelectedRowCount => _allSelected ? _shown.Count : _selected.Count;

    /// <summary>True when every row is selected, false when none is, null for some (the tri-state box of the list header).</summary>
    internal bool? AllSelectedState => SelectedRowCount == 0 ? false : SelectedRowCount == _shown.Count ? true : null;

    /// <summary>After every publish and after a select-mode change.</summary>
    internal event Action? SelectionStateChanged;

    internal void SelectAllRows()
    {
        if (IsReleased || _shown.Count == 0) return;
        var started = Stopwatch.GetTimestamp();
        _applying = true;
        try
        {
            using var all = Gtk.Bitset.NewRange(0, (uint)_shown.Count);
            Selection.SetSelection(all, all);
        }
        finally { _applying = false; }
        _lastChangedPosition = uint.MaxValue;
        ReadSelection();
        PublishSelection();
        Serilog.Log.Debug("Select all: {Rows} rows in {Milliseconds} ms", _shown.Count, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    internal void ClearSelection()
    {
        if (IsReleased) return;
        var started = Stopwatch.GetTimestamp();
        _applying = true;
        try { Selection.UnselectAll(); }
        finally { _applying = false; }
        _lastChangedPosition = uint.MaxValue;
        ReadSelection();
        PublishSelection();
        Serilog.Log.Debug("Clear selection in {Milliseconds} ms", (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    internal void SelectEntries(IReadOnlyList<MailListEntry> entries, bool scrollToFirst)
    {
        if (IsReleased) return;
        var positions = new List<uint>(entries.Count);
        foreach (var entry in entries)
        {
            if (PositionOf(entry) is { } position) positions.Add(position);
        }
        _applying = true;
        try { ListViews.Select(Selection, [.. positions]); }
        finally { _applying = false; }
        _lastChangedPosition = positions.Count == 1 ? positions[0] : uint.MaxValue;
        if (scrollToFirst && positions.Count > 0) ListViews.ScrollTo(List, positions.Min());
        ReadSelection();
        PublishSelection();
    }

    /// <summary>The selected entries in display order.</summary>
    internal MailListEntry[] SelectedEntries()
    {
        if (_allSelected) return [.. _shown];
        var entries = new List<MailListEntry>(_selected.Count);
        foreach (var position in ListViews.SelectedPositions(Selection))
        {
            if (position < _shown.Count) entries.Add(_shown[(int)position]);
        }
        return [.. entries];
    }

    internal bool IsEntrySelected(MailListEntry entry) => _allSelected ? PositionOf(entry) is not null : _selected.Contains(entry);

    internal void ToggleThread(MailListEntry entry)
    {
        if (IsReleased || entry.Row.Thread is not { } thread) return;
        if (entry.Row.IsExpanded) Projection.CollapseThread(thread.Key);
        else Projection.ExpandThread(thread.Key);
        ApplyNow();
    }

    /// <summary>The list keys without the hardware event; true when the key was handled.</summary>
    internal bool HandleListKey(uint keyval, Gdk.ModifierType state)
    {
        if (IsReleased) return false;
        var modifiers = state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask | Gdk.ModifierType.AltMask);
        if (keyval == KeyEscape)
        {
            if (SelectedRowCount == 0) return false;
            ClearSelection();
            return true;
        }
        if ((keyval == KeyDelete || keyval == KeyPadDelete) && modifiers == 0)
        {
            if (ViewModel.SelectedItemsCount == 0) return false;
            // A held Delete would delete message after message through auto-select-next.
            if (_deleteHeld) return true;
            _deleteHeld = true;
            ViewModel.ExecuteMailOperationCommand.Execute(MailOperation.SoftDelete);
            return true;
        }
        if (keyval == KeyA && modifiers == Gdk.ModifierType.ControlMask)
        {
            SelectAllRows();
            return true;
        }
        if (keyval == KeyZ && modifiers == Gdk.ModifierType.ControlMask)
        {
            Observe(ViewModel.UndoLatestQueuedActionCommand.ExecuteAsync(null));
            return true;
        }
        var right = keyval == KeyRight || keyval == KeyPadRight;
        var left = keyval == KeyLeft || keyval == KeyPadLeft;
        if ((right || left) && modifiers == 0 && SelectedRowCount == 1 && SelectedEntries() is [{ Row: { IsThreadHead: true, Thread.Count: > 1 } row } entry]
            && row.IsExpanded == left)
        {
            ToggleThread(entry);
            return true;
        }
        return false;
    }

    partial void BuildSelection()
    {
        var self = _self;
        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        Bindings.Own(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => self.Value?.HandleListKey(args.Keyval, args.State) ?? false));
        Bindings.Own(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyReleasedSignalArgs>(
            h => keys.OnKeyReleased += h, h => keys.OnKeyReleased -= h, (_, args) =>
            {
                if (self.Value is { } view && (args.Keyval == KeyDelete || args.Keyval == KeyPadDelete)) view._deleteHeld = false;
            }));
        List.AddController(keys);

        Bind.Bind(nameof(MailListPageViewModel.IsMultiSelectionModeEnabled), static vm => vm.IsMultiSelectionModeEnabled, ApplySelectionMode);
        Subscribe<ClearMailSelectionsRequested>(_ => ClearSelection());
        Bindings.Own(new ActionDisposable(() =>
        {
            UnwatchSelection();
            _multi?.Dispose();
            _movePopover?.Dispose();
        }));
    }

    partial void SetupSelectionCell(MailRowCell cell)
    {
        var self = _self;
        var cells = Cells;
        cell.Row.CheckToggled += _ =>
        {
            if (self.Value is { } view && cells.ItemOf(cell) is { } entry) view.ToggleEntry(entry);
        };
        cell.Row.ThreadToggleRequested += _ =>
        {
            if (self.Value is { } view && cells.ItemOf(cell) is { } entry) view.ToggleThread(entry);
        };

        // Select mode: a plain click toggles its row (capture phase, claimed); otherwise GTK's own click handling stays.
        var toggle = Gtk.GestureClick.New();
        toggle.SetButton(1);
        toggle.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        var toggleSignal = GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.PressedSignalArgs>(h => toggle.OnPressed += h, h => toggle.OnPressed -= h, (_, _) =>
        {
            if (self.Value is not { IsSelectionMode: true } view || cells.ItemOf(cell) is not { } entry) return;
            if ((toggle.GetCurrentEventState() & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask)) != 0) return;
            toggle.SetState(Gtk.EventSequenceState.Claimed);
            view.ToggleEntry(entry);
        });
        // A plain click on a thread head expands or collapses it (Windows); it runs after GTK selected the row.
        var thread = Gtk.GestureClick.New();
        thread.SetButton(1);
        var threadSignal = GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.ReleasedSignalArgs>(h => thread.OnReleased += h, h => thread.OnReleased -= h, (_, args) =>
        {
            if (args.NPress != 1 || self.Value is not { IsSelectionMode: false } || cells.ItemOf(cell) is not { Row.IsThreadHead: true } entry) return;
            if ((thread.GetCurrentEventState() & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask)) != 0) return;
            MainLoop.Post(() => self.Value?.ThreadHeadClicked(entry));
        });
        cell.Root.AddController(toggle);
        cell.Root.AddController(thread);
        _cellSelection.AddOrUpdate(cell, new ActionDisposable(() =>
        {
            toggleSignal.Dispose();
            threadSignal.Dispose();
        }));
    }

    partial void TeardownSelectionCell(MailRowCell cell)
    {
        if (_cellSelection.TryGetValue(cell, out var subscriptions)) subscriptions.Dispose();
        _cellSelection.Remove(cell);
    }

    partial void BindSelectionCell(MailRowCell cell, MailListEntry entry, bool isSelected)
    {
        cell.Row.IsSelectionMode = _selectionMode;
        cell.Row.IsChecked = isSelected;
    }

    partial void OnApplying(ref MailListApplyContext context)
    {
        // Removing the open message selects its neighbour when the preference asks for it (the macOS rule).
        var removed = context.PreviousRowCount - _next.Count;
        if (context.PreviousSelectionCount == 0 || context.PreviousFirstSelected < 0 || removed < 1 || removed > context.PreviousSelectionCount + 1) return;
        if (!ViewModel.PreferencesService.AutoSelectNextItem || ViewModel.IsInitializingFolder || _selectionMode) return;
        foreach (var entry in _selected)
        {
            if (entry.Pass == _pass) return;
        }
        if (_allSelected || _next.Count == 0) return;
        context.SelectAfter = _next[Math.Min(context.PreviousFirstSelected, _next.Count - 1)];
    }

    private void ApplySelectionMode(bool on)
    {
        if (IsReleased) return;
        _selectionMode = on;
        Cells.ForEachBound((cell, entry) =>
        {
            cell.Row.IsSelectionMode = on;
            cell.Row.IsChecked = IsEntrySelected(entry);
        });
        SelectionStateChanged?.Invoke();
    }

    private void ToggleEntry(MailListEntry entry)
    {
        if (PositionOf(entry) is not { } position) return;
        if (Selection.IsSelected(position)) Selection.UnselectItem(position);
        else Selection.SelectItem(position, false);
    }

    /// <summary>Windows: a collapsed thread expands and selects its representative message; an expanded one collapses.</summary>
    private void ThreadHeadClicked(MailListEntry entry)
    {
        if (IsReleased || PositionOf(entry) is null || entry.Row is not { IsThreadHead: true, Thread: { Count: > 1 } thread } row) return;
        if (row.IsExpanded)
        {
            Projection.CollapseThread(thread.Key);
            ApplyNow();
            return;
        }
        Projection.ExpandThread(thread.Key);
        ApplyNow();
        if (FindEntry("I:" + thread.RepresentativeItem.StableId.ToString("N")) is { } representative) SelectEntries([representative], scrollToFirst: false);
    }

    private void SelectionChanged(uint position, uint count)
    {
        if (_applying || _model.IsApplying || IsReleased) return;
        _lastChangedPosition = count == 1 ? position : uint.MaxValue;
        ReadSelection();
        PublishSelection();
    }

    private void ReadSelection()
    {
        using var set = Selection.GetSelection();
        var size = set.GetSize();
        _selected.Clear();
        _allSelected = size > 0 && size == (ulong)_shown.Count;
        if (_allSelected) return;
        for (uint index = 0; index < size; index++)
        {
            var position = set.GetNth(index);
            if (position < _shown.Count) _selected.Add(_shown[(int)position]);
        }
        if (_active is { } active && !_selected.Contains(active)) _active = null;
    }

    /// <summary>Selects again what was selected before an apply (and <paramref name="selectAfter"/>); GTK drops the selection of rows a splice moved.</summary>
    private void RestoreSelection(MailListEntry? selectAfter, List<MailListEntry> previous)
    {
        var expected = new List<uint>(_allSelected ? previous.Count : _selected.Count + 1);
        foreach (var entry in _allSelected ? previous : (IEnumerable<MailListEntry>)_selected)
        {
            if (PositionOf(entry) is { } position) expected.Add(position);
        }
        if (selectAfter is not null && PositionOf(selectAfter) is { } after && !expected.Contains(after)) expected.Add(after);
        expected.Sort();
        if (ListViews.SelectedPositions(Selection).AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(expected))) return;
        if (expected.Count > 0 && expected.Count == _shown.Count)
        {
            using var all = Gtk.Bitset.NewRange(0, (uint)_shown.Count);
            Selection.SetSelection(all, all);
        }
        else ListViews.Select(Selection, [.. expected]);
        if (selectAfter is not null) _lastChangedPosition = PositionOf(selectAfter) ?? uint.MaxValue;
    }

    private void PublishSelection()
    {
        var started = Stopwatch.GetTimestamp();
        List<MailListRow> rows;
        MailListRow? active = null;
        if (_allSelected)
        {
            rows = new List<MailListRow>(_shown.Count);
            foreach (var entry in _shown) rows.Add(entry.Row);
        }
        else
        {
            rows = new List<MailListRow>(_selected.Count);
            foreach (var position in ListViews.SelectedPositions(Selection))
            {
                if (position >= _shown.Count) continue;
                var entry = _shown[(int)position];
                rows.Add(entry.Row);
                if (position == _lastChangedPosition) _active = entry;
            }
        }
        if (_active is { } current && IsEntrySelected(current)) active = current.Row;
        var result = MailListSelectionSnapshotBuilder.Build(rows, Projection, active);
        if (result.Signature == _published) return;
        _published = result.Signature;
        ViewModel.ApplyMailSelectionSnapshot(result.Snapshot);
        MailListPerf.SelectionPublished(ViewModel.SelectedItemsCount, started);
        if (_selectionMode) Cells.ForEachBound((cell, entry) => cell.Row.IsChecked = IsEntrySelected(entry));
        UpdateMultiSelection();
        SelectionStateChanged?.Invoke();
    }

    private void UpdateMultiSelection()
    {
        if (IsReleased) return;
        var show = ViewModel.SelectedItemsCount > 1 && !ViewModel.HasSingleFullySelectedThread;
        UnwatchSelection();
        if (!show)
        {
            HideCover();
            return;
        }
        if (_multi is null)
        {
            var self = _self;
            _multi = new MailMultiSelectionView(_pictures);
            _multi.OperationRequested += (operation, anchor) => self.Value?.MultiSelectionOperation(operation, anchor);
            _multi.UnselectAllRequested += () => self.Value?.ViewModel.UnselectAllCommand.Execute(null);
        }
        RefreshMultiSelection();
        if (!_coverShown)
        {
            _coverShown = true;
            _navigation.SetReadingPaneCover(_multi.Root);
        }
        var items = ViewModel.SelectedItems;
        if (items.Count > WatchedSelectionLimit) return;
        if (_selectedItemChanged is null)
        {
            var self = _self;
            _multiRefresh = new MainLoop.Coalescer(() => self.Value?.RefreshMultiSelection());
            Bindings.Own(_multiRefresh);
            _selectedItemChanged = (_, args) =>
            {
                if (args.PropertyName is nameof(MailItemViewModel.IsRead) or nameof(MailItemViewModel.IsFlagged))
                    self.Value?.RunOnUI(() => self.Value?._multiRefresh?.Request());
            };
        }
        foreach (var item in items)
        {
            item.PropertyChanged += _selectedItemChanged;
            _watchedSelection.Add(item);
        }
    }

    /// <summary>Move opens the folder popover at its button (macOS); the other buttons run the shared command.</summary>
    private async void MultiSelectionOperation(MailOperation operation, Gtk.Widget anchor)
    {
        if (operation != MailOperation.Move)
        {
            ViewModel.ExecuteMailOperationCommand.Execute(operation);
            return;
        }
        try
        {
            _movePopover?.Dispose();
            _movePopover = null;
            var popover = await MoveFolderPopover.ShowForAsync(anchor, [.. ViewModel.SelectedItems], ViewModel, _folders, Dispatcher);
            if (IsReleased) popover?.Dispose();
            else _movePopover = popover;
        }
        catch (Exception exception) { if (!IsReleased) ReportError(exception); }
    }

    private void RefreshMultiSelection()
    {
        if (_multi is null || IsReleased) return;
        var items = ViewModel.SelectedItems;
        int unread = 0, flagged = 0, attachments = 0;
        var senders = new List<(string Name, string Address)>(3);
        foreach (var item in items)
        {
            if (!item.IsRead) unread++;
            if (item.IsFlagged) flagged++;
            if (item.HasAttachments) attachments++;
            if (senders.Count < 3 && !senders.Exists(sender => string.Equals(sender.Address, item.FromAddress, StringComparison.OrdinalIgnoreCase)))
                senders.Add((item.FromName, item.FromAddress));
        }
        _multi.Update(new MailMultiSelectionSummary(items.Count, unread, flagged, attachments, ViewModel.IsArchiveSpecialFolder, [.. senders]));
    }

    private void UnwatchSelection()
    {
        if (_selectedItemChanged is not null)
        {
            foreach (var item in _watchedSelection) item.PropertyChanged -= _selectedItemChanged;
        }
        _watchedSelection.Clear();
    }

    private void HideCover()
    {
        if (!_coverShown) return;
        _coverShown = false;
        _navigation.SetReadingPaneCover(null);
    }

    partial void OnDeactivating()
    {
        UnwatchSelection();
        HideCover();
    }

#if DEBUG
    internal string DebugMultiSelection => _coverShown && _multi is not null ? _multi.Describe() : string.Empty;

    internal void DebugToggle(uint position)
    {
        if (EntryAt(position) is { } entry) ToggleEntry(entry);
    }

    internal void DebugThreadClick(uint position)
    {
        if (EntryAt(position) is { } entry) ThreadHeadClicked(entry);
    }

    internal void DebugSelectPositions(uint[] positions)
    {
        var entries = new List<MailListEntry>(positions.Length);
        foreach (var position in positions)
        {
            if (EntryAt(position) is { } entry) entries.Add(entry);
        }
        SelectEntries(entries, scrollToFirst: false);
    }

    internal void DebugSetSelectionMode(bool on) => ViewModel.IsMultiSelectionModeEnabled = on;

    internal void DebugReleaseDelete() => _deleteHeld = false;
#endif
}
