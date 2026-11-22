using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Settings;

/// <summary>Lists that accept each other's rows. A list without a shared group reorders only itself.</summary>
public sealed class WinoDragGroup
{
    /// <summary>(source, target): may a row of source be dropped on target? Default: the same list only.</summary>
    public Func<WinoDragList, WinoDragList, bool> CanDrop { get; set; } = static (source, target) => ReferenceEquals(source, target);

    /// <summary>(source, from, target, to); <c>to</c> is the item's index after the move.</summary>
    public Action<WinoDragList, int, WinoDragList, int>? Dropped { get; set; }
}

/// <param name="Name">The accessible name of the handle and the move buttons.</param>
public sealed record WinoDragRow(Gtk.ListBoxRow Row, string Name);

/// <summary>
/// A boxed list of caller-built rows that reorders by pointer drag, Alt+Up / Alt+Down and optional Move up / Move down
/// buttons. It never moves a row itself: every path raises <see cref="WinoDragGroup.Dropped"/> (and
/// <see cref="Reordered"/> for a same-list move); the owner changes its collection and calls <see cref="SetRows"/>.
/// Main thread only.
/// </summary>
public sealed class WinoDragList : IDisposable
{
    private const int AutoscrollMargin = 32;
    private const int AutoscrollStep = 12;

    private readonly Gtk.ListBox _list = Gtk.ListBox.New();
    private readonly Dictionary<Gtk.ListBoxRow, RowState> _states = new(ReferenceEqualityComparer.Instance);
    private readonly List<WinoDragRow> _rows = [];
    private readonly List<IDisposable> _subscriptions = [];
    private Gtk.Widget? _empty;
    private Gtk.ListBoxRow? _marked;
    private bool _disposed;

    public WinoDragList(WinoDragGroup? group = null, string? accessibleLabel = null)
    {
        Group = group ?? new WinoDragGroup();
        _list.AddCssClass("boxed-list");
        _list.SetSelectionMode(Gtk.SelectionMode.None);
        if (!string.IsNullOrEmpty(accessibleLabel)) WinoA11y.Label(_list, accessibleLabel);

        var target = Gtk.DropTarget.New(GObject.Type.String, Gdk.DragAction.Move);
        _subscriptions.Add(GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.EnterSignalArgs, Gdk.DragAction>(
            h => target.OnEnter += h, h => target.OnEnter -= h, (_, args) => Hover(args.Y), 0));
        _subscriptions.Add(GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.MotionSignalArgs, Gdk.DragAction>(
            h => target.OnMotion += h, h => target.OnMotion -= h, (_, args) => Hover(args.Y), 0));
        _subscriptions.Add(GtkSignals.Connect<Gtk.DropTarget>(h => target.OnLeave += h, h => target.OnLeave -= h, (_, _) => Mark(null, false)));
        _subscriptions.Add(GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.DropSignalArgs, bool>(
            h => target.OnDrop += h, h => target.OnDrop -= h, (_, args) => Drop(args.Value.GetString(), args.Y), false));
        _list.AddController(target);
        _subscriptions.Add(new ActionDisposable(() => _list.RemoveController(target)));

        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        _subscriptions.Add(GtkSignals.ConnectReturning<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => KeyPressed(args.Keyval, args.State), false));
        _list.AddController(keys);
        _subscriptions.Add(new ActionDisposable(() => _list.RemoveController(keys)));
    }

    public Gtk.Widget Root => _list;

    public WinoDragGroup Group { get; }

    /// <summary>The owner's key for this list (a section, a parent item).</summary>
    public object? Tag { get; set; }

    public int Count => _rows.Count;

    /// <summary>Set before the first <see cref="SetRows"/>.</summary>
    public bool ShowsHandles { get; set; } = true;

    /// <summary>Set before the first <see cref="SetRows"/>.</summary>
    public bool ShowsMoveButtons { get; set; }

    /// <summary>Same-list move (from, to), raised after <see cref="WinoDragGroup.Dropped"/>.</summary>
    public event Action<int, int>? Reordered;

    /// <summary>Replaces the content; rows may be reused between calls. A row must not have another parent.</summary>
    public void SetRows(IReadOnlyList<WinoDragRow> rows)
    {
        if (_disposed) return;
        Mark(null, false);
        var keep = new HashSet<Gtk.ListBoxRow>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows) keep.Add(row.Row);
        foreach (var old in _rows)
        {
            if (keep.Contains(old.Row)) continue;
            if (_states.Remove(old.Row, out var state)) state.Dispose();
        }
        foreach (var old in _rows) _list.Remove(old.Row);
        if (_empty is not null && _empty.GetParent() is not null) _list.Remove(_empty);

        _rows.Clear();
        _rows.AddRange(rows);
        foreach (var row in _rows)
        {
            if (!_states.TryGetValue(row.Row, out var state)) _states[row.Row] = state = Decorate(row);
            state.Name = row.Name;
            _list.Append(row.Row);
        }
        if (_rows.Count == 0 && _empty is not null) _list.Append(_empty);
        RefreshButtons();
    }

    /// <summary>Shown, and accepts drops at index 0, while the list is empty.</summary>
    public void SetEmptyRow(Gtk.Widget? row)
    {
        if (_empty is not null && _empty.GetParent() is not null) _list.Remove(_empty);
        _empty = row;
        if (_empty is not null && _rows.Count == 0) _list.Append(_empty);
    }

    /// <summary>The key, button and Debug path; the same checks and events as a drop.</summary>
    public bool Move(int from, int to)
    {
        if (from < 0 || from >= _rows.Count || to < 0 || to >= _rows.Count || from == to) return false;
        if (!Group.CanDrop(this, this)) return false;
        Raise(this, from, this, to);
        return true;
    }

    public bool MoveTo(int from, WinoDragList target, int to)
    {
        if (ReferenceEquals(target, this)) return Move(from, to);
        if (from < 0 || from >= _rows.Count || to < 0 || to > target.Count) return false;
        if (!ReferenceEquals(target.Group, Group) || !Group.CanDrop(this, target)) return false;
        Raise(this, from, target, to);
        return true;
    }

    public void FocusRow(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        _rows[index].Row.GrabFocus();
    }

    public string Describe() => $"{Tag}|{_rows.Count}|{string.Join(", ", _rows.Select(row => row.Name))}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var state in _states.Values) state.Dispose();
        _states.Clear();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        Reordered = null;
        DragRowPayload.Forget(this);
    }

    private RowState Decorate(WinoDragRow row)
    {
        var state = new RowState { Name = row.Name };
        var action = row.Row as Adw.ActionRow;
        Gtk.Widget sourceWidget = row.Row;
        if (action is not null && ShowsHandles)
        {
            var handle = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            handle.AddCssClass(WinoCssClass.DragHandle);
            handle.SetValign(Gtk.Align.Center);
            handle.Append(WinoIcons.Label(WinoIconGlyph.ReOrderDotsVertical, 16));
            handle.SetTooltipText(row.Name);
            action.AddPrefix(handle);
            state.Decorations.Add(() => action.Remove(handle));
            sourceWidget = handle;
        }

        if (ShowsHandles || action is null)
        {
            var source = Gtk.DragSource.New();
            source.SetActions(Gdk.DragAction.Move);
            var rowWidget = row.Row;
            state.Subscriptions.Add(GtkSignals.ConnectReturning<Gtk.DragSource, Gtk.DragSource.PrepareSignalArgs, Gdk.ContentProvider?>(
                h => source.OnPrepare += h, h => source.OnPrepare -= h, (_, _) => Prepare(rowWidget), null));
            state.Subscriptions.Add(GtkSignals.Connect<Gtk.DragSource, Gtk.DragSource.DragBeginSignalArgs>(
                h => source.OnDragBegin += h, h => source.OnDragBegin -= h, (sender, _) =>
                {
                    sender.SetIcon(Gtk.WidgetPaintable.New(rowWidget), 12, 12);
                    rowWidget.AddCssClass(WinoCssClass.Dragging);
                }));
            state.Subscriptions.Add(GtkSignals.Connect<Gtk.DragSource, Gtk.DragSource.DragEndSignalArgs>(
                h => source.OnDragEnd += h, h => source.OnDragEnd -= h, (_, _) => EndDrag(rowWidget)));
            state.Subscriptions.Add(GtkSignals.ConnectReturning<Gtk.DragSource, Gtk.DragSource.DragCancelSignalArgs, bool>(
                h => source.OnDragCancel += h, h => source.OnDragCancel -= h, (_, _) =>
                {
                    EndDrag(rowWidget);
                    return false;
                }, false));
            sourceWidget.AddController(source);
            state.Decorations.Add(() => sourceWidget.RemoveController(source));
        }

        if (action is not null && ShowsMoveButtons)
        {
            var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            buttons.SetValign(Gtk.Align.Center);
            state.Up = WinoIcons.Button(WinoIconGlyph.ChevronUp, $"{Translator.MailFilters_MoveUp}: {row.Name}", 12);
            state.Down = WinoIcons.Button(WinoIconGlyph.ChevronDown, $"{Translator.MailFilters_MoveDown}: {row.Name}", 12);
            buttons.Append(state.Up);
            buttons.Append(state.Down);
            action.AddSuffix(buttons);
            var rowWidget = row.Row;
            state.Subscriptions.Add(GtkSignals.OnClicked(state.Up, () => MoveRow(rowWidget, -1)));
            state.Subscriptions.Add(GtkSignals.OnClicked(state.Down, () => MoveRow(rowWidget, 1)));
            state.Decorations.Add(() => action.Remove(buttons));
        }
        return state;
    }

    private void MoveRow(Gtk.ListBoxRow row, int offset)
    {
        var index = IndexOf(row);
        if (index >= 0) Move(index, index + offset);
    }

    private int IndexOf(Gtk.ListBoxRow row) => _rows.FindIndex(entry => ReferenceEquals(entry.Row, row));

    private void RefreshButtons()
    {
        for (var index = 0; index < _rows.Count; index++)
        {
            if (!_states.TryGetValue(_rows[index].Row, out var state)) continue;
            var canMove = Group.CanDrop(this, this);
            state.Up?.SetSensitive(canMove && index > 0);
            state.Down?.SetSensitive(canMove && index < _rows.Count - 1);
            if (state.Up is not null) WinoA11y.Label(state.Up, $"{Translator.MailFilters_MoveUp}: {state.Name}");
            if (state.Down is not null) WinoA11y.Label(state.Down, $"{Translator.MailFilters_MoveDown}: {state.Name}");
        }
    }

    private Gdk.ContentProvider? Prepare(Gtk.ListBoxRow row)
    {
        var index = IndexOf(row);
        if (index < 0) return null;
        return Gdk.ContentProvider.NewForValue(new GObject.Value(DragRowPayload.Put(this, index)));
    }

    private void EndDrag(Gtk.ListBoxRow row)
    {
        row.RemoveCssClass(WinoCssClass.Dragging);
        Mark(null, false);
        DragRowPayload.Clear();
    }

    private bool Accepts(DragRowPayload.Entry? current)
        => current is { } entry && ReferenceEquals(entry.List.Group, Group) && Group.CanDrop(entry.List, this);

    private Gdk.DragAction Hover(double y)
    {
        if (!Accepts(DragRowPayload.Current))
        {
            Mark(null, false);
            return 0;
        }
        var (_, row, after) = Slot(y);
        Mark(row, after);
        Autoscroll(y);
        return Gdk.DragAction.Move;
    }

    private bool Drop(string? text, double y)
    {
        Mark(null, false);
        if (DragRowPayload.Take(text) is not { } entry || !Accepts(entry)) return false;
        var (insert, _, _) = Slot(y);
        var source = entry.List;
        var from = entry.Index;
        if (from < 0 || from >= source.Count) return false;
        var to = ReferenceEquals(source, this) && insert > from ? insert - 1 : insert;
        if (ReferenceEquals(source, this) && to == from) return true;
        Raise(source, from, this, to);
        return true;
    }

    private (int Insert, Gtk.ListBoxRow? Row, bool After) Slot(double y)
    {
        if (_rows.Count == 0) return (0, null, false);
        if (_list.GetRowAtY((int)y) is not { } row || IndexOf(row) is var index && index < 0)
            return (_rows.Count, _rows[^1].Row, true);
        var after = row.ComputeBounds(_list, out var rect) && y > rect.GetY() + rect.GetHeight() / 2;
        return (after ? index + 1 : index, row, after);
    }

    private void Mark(Gtk.ListBoxRow? row, bool after)
    {
        if (_marked is not null)
        {
            _marked.RemoveCssClass(WinoCssClass.DropBefore);
            _marked.RemoveCssClass(WinoCssClass.DropAfter);
        }
        _marked = row;
        row?.AddCssClass(after ? WinoCssClass.DropAfter : WinoCssClass.DropBefore);
    }

    private void Autoscroll(double y)
    {
        if (_list.GetAncestor(Gtk.ScrolledWindow.GetGType()) is not Gtk.ScrolledWindow scroller) return;
        if (!_list.ComputeBounds(scroller, out var rect)) return;
        var adjustment = scroller.GetVadjustment();
        var inScroller = y + rect.GetY();
        var height = scroller.GetHeight();
        if (inScroller < AutoscrollMargin) adjustment.SetValue(adjustment.GetValue() - AutoscrollStep);
        else if (inScroller > height - AutoscrollMargin) adjustment.SetValue(adjustment.GetValue() + AutoscrollStep);
    }

    private bool KeyPressed(uint keyval, Gdk.ModifierType state)
    {
        if ((state & (Gdk.ModifierType.AltMask | Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask)) != Gdk.ModifierType.AltMask) return false;
        var offset = keyval switch
        {
            Gdk.Constants.KEY_Up or Gdk.Constants.KEY_KP_Up => -1,
            Gdk.Constants.KEY_Down or Gdk.Constants.KEY_KP_Down => 1,
            _ => 0,
        };
        if (offset == 0) return false;
        if (FocusedIndex() is var index && index >= 0) Move(index, index + offset);
        return true;
    }

    private int FocusedIndex()
    {
        var focus = _list.GetFocusChild();
        for (var index = 0; index < _rows.Count; index++)
        {
            if (focus is not null && ReferenceEquals(_rows[index].Row, focus)) return index;
        }
        return -1;
    }

    private void Raise(WinoDragList source, int from, WinoDragList target, int to)
    {
        try
        {
            Group.Dropped?.Invoke(source, from, target, to);
            if (ReferenceEquals(source, target)) source.Reordered?.Invoke(from, to);
        }
        catch (Exception exception)
        {
            if (GtkSignals.HandlerFailed is { } report) report(exception);
            else Console.Error.WriteLine(exception);
        }
        MainLoop.Post(() =>
        {
            if (!target._disposed) target.FocusRow(to);
        });
    }

    private sealed class RowState : IDisposable
    {
        public string Name = string.Empty;
        public Gtk.Button? Up;
        public Gtk.Button? Down;
        public readonly List<IDisposable> Subscriptions = [];
        public readonly List<Action> Decorations = [];

        public void Dispose()
        {
            foreach (var subscription in Subscriptions) subscription.Dispose();
            Subscriptions.Clear();
            foreach (var undo in Decorations) undo();
            Decorations.Clear();
            Up = null;
            Down = null;
        }
    }

    /// <summary>The dragged row lives in process: the drag carries only a single-use token.</summary>
    private static class DragRowPayload
    {
        public const string Prefix = "app.winomail.dragrow:";

        public readonly record struct Entry(WinoDragList List, int Index, string Token);

        public static Entry? Current { get; private set; }

        public static string Put(WinoDragList list, int index)
        {
            var token = Guid.NewGuid().ToString("N");
            Current = new Entry(list, index, token);
            return Prefix + token;
        }

        public static Entry? Take(string? text)
        {
            if (Current is not { } entry || text is null || text != Prefix + entry.Token) return null;
            Current = null;
            return entry;
        }

        public static void Clear() => Current = null;

        public static void Forget(WinoDragList list)
        {
            if (Current is { } entry && ReferenceEquals(entry.List, list)) Current = null;
        }
    }
}
