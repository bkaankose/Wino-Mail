using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Common;

internal enum EndPaneState { Wide, Shared, Swapped }

/// <summary>
/// A flexible start pane and a fixed-width end pane (contact detail, task drawer) that adapt to the page's own width,
/// not the window's: a wide sidebar in a small window must not squeeze the list. Wide shows both panes side by side,
/// Shared splits the page in halves while the end pane has something to show, Swapped shows one pane at a time.
/// </summary>
internal sealed class EndPaneLayout : IDisposable
{
    private const int SharedEndMinimum = 280;

    private readonly int _endWidth;
    private readonly Adw.BreakpointBin _root = Adw.BreakpointBin.New();
    private readonly Gtk.Box _box = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly Adw.Bin _start = Adw.Bin.New();
    private readonly Gtk.Box _endColumn = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly Gtk.Separator _separator = Gtk.Separator.New(Gtk.Orientation.Vertical);
    private readonly Adw.Bin _end = Adw.Bin.New();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly bool _swapFollowsNotWide;
    private bool _notWide;
    private bool _swap;
    private bool _showsEnd;
    private bool _disposed;

    /// <param name="endWidth">Width of the end pane in the Wide state.</param>
    /// <param name="wideMinimum">The smallest page width that is Wide.</param>
    /// <param name="swapMaximum">The largest page width that is Swapped; below <paramref name="wideMinimum"/>.</param>
    public EndPaneLayout(int endWidth, int wideMinimum, int swapMaximum)
    {
        _endWidth = endWidth;
        _root.SetSizeRequest(320, 300);
        _start.SetHexpand(true);
        _start.SetVexpand(true);
        _end.SetVexpand(true);
        _end.SetHexpand(true);
        // The separator shares a column with the end pane, so a homogeneous split gives the two panes halves.
        _endColumn.Append(_separator);
        _endColumn.Append(_end);
        _box.Append(_start);
        _box.Append(_endColumn);
        _root.SetChild(_box);
        // Without a Shared range (To Do) every width below Wide swaps.
        _swapFollowsNotWide = swapMaximum >= wideMinimum - 1;
        AddBreakpoint(wideMinimum - 1, value => _notWide = value);
        if (!_swapFollowsNotWide) AddBreakpoint(swapMaximum, value => _swap = value);
        Apply();
    }

    public Gtk.Widget Root => _root;

    public Gtk.Widget? Start
    {
        get => _start.GetChild();
        set => _start.SetChild(value);
    }

    public Gtk.Widget? End
    {
        get => _end.GetChild();
        set => _end.SetChild(value);
    }

    /// <summary>The end pane stays visible in the Wide state with nothing selected (it shows its empty state).</summary>
    public bool KeepsEndWhenWide { get; init; }

    public bool ShowsEnd
    {
        get => _showsEnd;
        set
        {
            if (_showsEnd == value) return;
            _showsEnd = value;
            Apply();
        }
    }

    public EndPaneState State { get; private set; } = EndPaneState.Wide;

    public event Action? StateChanged;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        StateChanged = null;
    }

    private void AddBreakpoint(int maxWidth, Action<bool> set)
    {
        var breakpoint = Adw.Breakpoint.New(Adw.BreakpointCondition.NewLength(Adw.BreakpointConditionLengthType.MaxWidth, maxWidth, Adw.LengthUnit.Px));
        _subscriptions.Add(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnApply += h, h => breakpoint.OnApply -= h, (_, _) =>
        {
            set(true);
            Apply();
        }));
        _subscriptions.Add(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnUnapply += h, h => breakpoint.OnUnapply -= h, (_, _) =>
        {
            set(false);
            Apply();
        }));
        _root.AddBreakpoint(breakpoint);
    }

    private void Apply()
    {
        if (_disposed) return;
        var swap = _swapFollowsNotWide ? _notWide : _swap;
        var state = swap ? EndPaneState.Swapped : _notWide ? EndPaneState.Shared : EndPaneState.Wide;
        switch (state)
        {
            case EndPaneState.Wide:
                var showEnd = KeepsEndWhenWide || _showsEnd;
                _box.SetHomogeneous(false);
                _start.SetVisible(true);
                _endColumn.SetVisible(showEnd);
                _separator.SetVisible(true);
                _endColumn.SetHexpand(false);
                _endColumn.SetSizeRequest(_endWidth, -1);
                break;
            case EndPaneState.Shared:
                _box.SetHomogeneous(_showsEnd);
                _start.SetVisible(true);
                _endColumn.SetVisible(_showsEnd);
                _separator.SetVisible(true);
                _endColumn.SetHexpand(true);
                _endColumn.SetSizeRequest(SharedEndMinimum, -1);
                break;
            default:
                _box.SetHomogeneous(false);
                _start.SetVisible(!_showsEnd);
                _endColumn.SetVisible(_showsEnd);
                _separator.SetVisible(false);
                _endColumn.SetHexpand(true);
                _endColumn.SetSizeRequest(-1, -1);
                break;
        }
        if (state == State) return;
        State = state;
        StateChanged?.Invoke();
    }
}
