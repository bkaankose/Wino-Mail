using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// A list beside a detail pane with a divider the user moves. Below the sum of both minimums the panes stack and only one
/// of them is visible; the list shrinks toward its minimum before that happens.
/// </summary>
internal sealed class ListDetailSplit : IDisposable
{
    private readonly Adw.BreakpointBin _bin = Adw.BreakpointBin.New();
    private readonly Adw.Breakpoint _breakpoint;
    private readonly Gtk.Paned _paned = Gtk.Paned.New(Gtk.Orientation.Horizontal);
    private readonly Adw.Bin _listHost = Adw.Bin.New();
    private readonly Adw.Bin _detailHost = Adw.Bin.New();
    private readonly Gtk.GestureClick _press = Gtk.GestureClick.New();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly MainLoop.Coalescer _restore;
    private readonly MainLoop.Throttle _keyboardCommit;
    private readonly int _listMinimum;
    private int _position;
    private bool _stacked;
    private bool _showsDetail;
    private bool _pressed;
    private bool _dirty;
    private bool _applying;
    private bool _disposed;

    public ListDetailSplit(int listMinimum, int detailMinimum, int position)
    {
        _listMinimum = listMinimum;
        _position = Math.Max(listMinimum, position);
        // The divider takes one pixel, which comes out of the list so both minimums still fit in their sum.
        _listHost.SetSizeRequest(listMinimum - 1, -1);
        _detailHost.SetSizeRequest(detailMinimum, -1);
        _paned.SetStartChild(_listHost);
        _paned.SetEndChild(_detailHost);
        _paned.SetResizeStartChild(false);
        _paned.SetResizeEndChild(true);
        _paned.SetShrinkStartChild(false);
        _paned.SetShrinkEndChild(false);
        _paned.SetWideHandle(false);
        _paned.SetPosition(_position);

        _bin.SetSizeRequest(Math.Min(listMinimum, detailMinimum), 200);
        _bin.SetChild(_paned);
        _breakpoint = Adw.Breakpoint.New(Adw.BreakpointCondition.NewLength(Adw.BreakpointConditionLengthType.MaxWidth, listMinimum + detailMinimum - 1, Adw.LengthUnit.Px));
        var breakpoint = _breakpoint;
        _subscriptions.Add(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnApply += h, h => breakpoint.OnApply -= h, (_, _) => SetStacked(true)));
        _subscriptions.Add(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnUnapply += h, h => breakpoint.OnUnapply -= h, (_, _) => SetStacked(false)));
        _bin.AddBreakpoint(_breakpoint);

        _restore = new MainLoop.Coalescer(Restore);
        _keyboardCommit = new MainLoop.Throttle(400, Commit);
        var press = _press;
        press.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        press.SetButton(1);
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.PressedSignalArgs>(h => press.OnPressed += h, h => press.OnPressed -= h, (_, _) => _pressed = true));
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.ReleasedSignalArgs>(h => press.OnReleased += h, h => press.OnReleased -= h, (_, _) => Released()));
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureClick>(h => press.OnStopped += h, h => press.OnStopped -= h, (_, _) => Released()));
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.UnpairedReleaseSignalArgs>(h => press.OnUnpairedRelease += h, h => press.OnUnpairedRelease -= h, (_, _) => Released()));
        _paned.AddController(press);
        _subscriptions.Add(GtkSignals.OnNotify(_paned, "position", PositionChanged));
        _subscriptions.Add(GtkSignals.OnNotify(_paned, "max-position", _restore.Request));
    }

    public Gtk.Widget Root => _bin;

    public Gtk.Widget? List
    {
        get => _listHost.GetChild();
        set => _listHost.SetChild(value);
    }

    public Gtk.Widget? Detail
    {
        get => _detailHost.GetChild();
        set => _detailHost.SetChild(value);
    }

    /// <summary>The remembered list width; the visible list is narrower when the detail pane needs the room.</summary>
    public int Position
    {
        get => _position;
        set
        {
            _position = Math.Max(_listMinimum, value);
            _restore.Request();
        }
    }

    public int MeasuredListWidth => _listHost.GetVisible() ? _listHost.GetWidth() : 0;
    public int MeasuredDetailWidth => _detailHost.GetVisible() ? _detailHost.GetWidth() : 0;
    public bool IsStacked => _stacked;

    public bool ShowsDetail
    {
        get => _showsDetail;
        set
        {
            if (_showsDetail == value) return;
            _showsDetail = value;
            if (_stacked) ApplyVisibility();
            StateChanged?.Invoke();
        }
    }

    public event Action? PositionCommitted;
    public event Action? StateChanged;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _restore.Dispose();
        _keyboardCommit.Dispose();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _paned.RemoveController(_press);
        _bin.RemoveBreakpoint(_breakpoint);
        PositionCommitted = null;
        StateChanged = null;
    }

    private void SetStacked(bool stacked)
    {
        if (_stacked == stacked) return;
        _stacked = stacked;
        ApplyVisibility();
        StateChanged?.Invoke();
    }

    private void ApplyVisibility()
    {
        var showList = !_stacked || !_showsDetail;
        var showDetail = !_stacked || _showsDetail;
        // GTK leaves the focus on the paned when the focused pane hides, and the paned later hands it to whatever comes
        // next in the window (the sidebar, which selects the row it lands on). The focus moves to the visible pane instead.
        var moveFocus = _paned.IsFocus() || (!showList && HasFocusWithin(_listHost)) || (!showDetail && HasFocusWithin(_detailHost));
        _applying = true;
        try
        {
            _listHost.SetVisible(showList);
            _detailHost.SetVisible(showDetail);
        }
        finally { _applying = false; }
        if (moveFocus) (showDetail && _stacked ? _detailHost : _listHost).ChildFocus(Gtk.DirectionType.TabForward);
        if (!_stacked) _restore.Request();
    }

    private static bool HasFocusWithin(Gtk.Widget widget) => (widget.GetStateFlags() & Gtk.StateFlags.FocusWithin) != 0;

    // Clamping during allocation and the collapsed child of the stacked layout also move the divider; only a press on the
    // paned or keyboard focus on it marks a move as the user's.
    private void PositionChanged()
    {
        if (_applying || _stacked || _disposed) return;
        if (_pressed)
        {
            _position = _paned.GetPosition();
            _dirty = true;
        }
        else if (_paned.IsFocus())
        {
            _position = _paned.GetPosition();
            _dirty = true;
            _keyboardCommit.Request();
        }
    }

    private void Released()
    {
        if (!_pressed) return;
        _pressed = false;
        Commit();
    }

    private void Commit()
    {
        if (!_dirty || _disposed) return;
        _dirty = false;
        PositionCommitted?.Invoke();
    }

    private void Restore()
    {
        if (_stacked || _pressed || _disposed) return;
        var target = Math.Min(_position, _paned.MaxPosition);
        if (_paned.GetPosition() == target) return;
        _applying = true;
        try { _paned.SetPosition(target); }
        finally { _applying = false; }
    }
}
