using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Shell;

internal enum ShellWidthClass
{
    Wide,
    Medium,
    Compact
}

/// <summary>
/// The panes of the shell: a sidebar the user resizes and hides, docked from <see cref="ShellMetrics.SidebarDockedMinimumShellWidth"/>
/// and an overlay below it, and a content area under one header. A page that uses the reading pane sits in a
/// <see cref="ListDetailSplit"/> beside it; any other page gets the whole content area.
/// </summary>
internal sealed class ShellLayout : IDisposable
{
    private const string SplitPage = "split";
    private const string FullPage = "full";
    private const string EmptyPage = "empty";

    private readonly IPreferencesService _preferences;
    private readonly IStatePersistanceService _state;
    private readonly WindowStateStore _store;
    private readonly Adw.BreakpointBin _root = Adw.BreakpointBin.New();
    private readonly Adw.OverlaySplitView _split = Adw.OverlaySplitView.New();
    private readonly Adw.Bin _sidebarHost = Adw.Bin.New();
    private readonly Adw.Bin _headerHost = Adw.Bin.New();
    private readonly Gtk.Stack _pages = Gtk.Stack.New();
    private readonly Adw.Bin _listHost = Adw.Bin.New();
    private readonly Adw.Bin _readerHost = Adw.Bin.New();
    private readonly Adw.Bin _fullHost = Adw.Bin.New();
    private readonly Adw.Bin _emptyHost = Adw.Bin.New();
    private readonly Gtk.Widget _emptyZone;
    private readonly PaneResizeHandle _handle;
    private readonly ListDetailSplit _listSplit;
    private readonly Adw.Breakpoint _medium;
    private readonly Adw.Breakpoint _compact;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _mediumApplied;
    private bool _compactApplied;
    private bool _hasWidthClass;
    private int _sidebarWidth;
    private bool _disposed;

    public ShellLayout(IPreferencesService preferences, IStatePersistanceService state, WindowStateStore store)
    {
        _preferences = preferences;
        _state = state;
        _store = store;

        _handle = new PaneResizeHandle(_split, Translator.Accessibility_ResizeNavigationPane);
        _handle.Dragged += width => SetSidebarWidth(width, persist: false);
        _handle.DragEnded += () => _store.SidebarWidth = _sidebarWidth;
        var sidebar = Gtk.Overlay.New();
        sidebar.SetChild(_sidebarHost);
        sidebar.AddOverlay(_handle.Root);

        _split.SetSidebarWidthUnit(Adw.LengthUnit.Px);
        _split.SetEnableShowGesture(true);
        _split.SetEnableHideGesture(true);
        _split.SetPinSidebar(false);
        _split.SetSidebar(sidebar);
        _sidebarWidth = ShellMetrics.SidebarDefaultWidth;
        _split.SetMaxSidebarWidth(_sidebarWidth);
        _split.SetMinSidebarWidth(_sidebarWidth);
        SetSidebarWidth(store.SidebarWidth, persist: false);

        _listSplit = new ListDetailSplit(ShellMetrics.ListMinimumWidth, ShellMetrics.ReaderMinimumWidth, (int)Math.Round(state.MailListPaneLength));
        _listSplit.List = WinoZone.Create(_listHost, WinoThemeSurface.Workspace);
        _listSplit.Detail = WinoZone.Create(_readerHost, WinoThemeSurface.ReadingPane);
        _listSplit.PositionCommitted += () => _state.MailListPaneLength = _listSplit.Position;
        _listSplit.StateChanged += () =>
        {
            _state.IsReaderNarrowed = _listSplit.IsStacked && _listSplit.ShowsDetail;
            StateChanged?.Invoke();
        };

        _emptyZone = WinoZone.Create(null, WinoThemeSurface.Workspace);
        _emptyHost.SetChild(_emptyZone);
        _pages.SetTransitionType(Gtk.StackTransitionType.None);
        _pages.AddNamed(_listSplit.Root, SplitPage);
        _pages.AddNamed(WinoZone.Create(_fullHost, WinoThemeSurface.Workspace), FullPage);
        _pages.AddNamed(_emptyHost, EmptyPage);
        _pages.SetVisibleChildName(EmptyPage);

        var content = Adw.ToolbarView.New();
        content.AddTopBar(_headerHost);
        content.SetContent(_pages);
        _split.SetContent(content);

        _root.SetSizeRequest(ShellMetrics.WindowMinimumWidth, ShellMetrics.WindowMinimumHeight);
        _root.SetChild(_split);
        _medium = AddBreakpoint(ShellMetrics.SidebarDockedMinimumShellWidth - 1, applied => _mediumApplied = applied);
        _compact = AddBreakpoint(ShellMetrics.HeaderCompactMaximumShellWidth - 1, applied => _compactApplied = applied);
        _subscriptions.Add(GtkSignals.OnNotify(_split, "show-sidebar", () => StateChanged?.Invoke()));
        ApplyWidthClass();
    }

    public Gtk.Widget Root => _root;

    public Gtk.Widget ReadingPane
    {
        set => _readerHost.SetChild(value);
    }

    public ShellWidthClass WidthClass { get; private set; }
    public bool IsSidebarOverlay => WidthClass != ShellWidthClass.Wide;
    public bool IsSidebarVisible => _split.GetShowSidebar();
    public int SidebarWidth => _sidebarWidth;
    public int MeasuredSidebarWidth => _split.GetShowSidebar() ? _sidebarHost.GetWidth() : 0;
    public bool IsStacked => _listSplit.IsStacked && _pages.GetVisibleChildName() == SplitPage;
    public bool ShowsDetail => _listSplit.ShowsDetail;
    public int ListWidth => _listSplit.Position;
    public int MeasuredListWidth => _listSplit.MeasuredListWidth;
    public int MeasuredReaderWidth => _listSplit.MeasuredDetailWidth;
    public event Action? StateChanged;

    public void SetSidebar(Gtk.Widget sidebar) => _sidebarHost.SetChild(sidebar);

    public void SetHeader(Gtk.Widget? header) => _headerHost.SetChild(header);

    public void SetContent(Gtk.Widget? page, bool usesReadingPane)
    {
        _listHost.SetChild(null);
        _fullHost.SetChild(null);
        if (page is null) _pages.SetVisibleChildName(EmptyPage);
        else if (usesReadingPane)
        {
            _listHost.SetChild(page);
            _pages.SetVisibleChildName(SplitPage);
        }
        else
        {
            _fullHost.SetChild(page);
            _pages.SetVisibleChildName(FullPage);
        }
        StateChanged?.Invoke();
    }

    public void SetPlaceholder(Gtk.Widget? placeholder) => _emptyHost.SetChild(placeholder ?? _emptyZone);

    public void ToggleSidebar()
    {
        var show = !_split.GetShowSidebar();
        _split.SetShowSidebar(show);
        if (WidthClass == ShellWidthClass.Wide) _preferences.IsNavigationPaneOpened = show;
    }

    public void DismissOverlaySidebar()
    {
        if (IsSidebarOverlay && _split.GetShowSidebar()) _split.SetShowSidebar(false);
    }

    public void SetSidebarWidth(int width, bool persist)
    {
        var clamped = WindowStateStore.ClampSidebar(width);
        if (clamped != _sidebarWidth)
        {
            // libadwaita logs a critical while the minimum exceeds the maximum, so the order follows the direction.
            if (clamped > _sidebarWidth)
            {
                _split.SetMaxSidebarWidth(clamped);
                _split.SetMinSidebarWidth(clamped);
            }
            else
            {
                _split.SetMinSidebarWidth(clamped);
                _split.SetMaxSidebarWidth(clamped);
            }
            _sidebarWidth = clamped;
            StateChanged?.Invoke();
        }
        if (persist) _store.SidebarWidth = clamped;
    }

    public void ShowDetail(bool show) => _listSplit.ShowsDetail = show;

    public void SetListWidth(int width, bool persist)
    {
        _listSplit.Position = width;
        if (persist) _state.MailListPaneLength = _listSplit.Position;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _root.RemoveBreakpoint(_compact);
        _root.RemoveBreakpoint(_medium);
        _handle.Dispose();
        _listSplit.Dispose();
        StateChanged = null;
    }

    private Adw.Breakpoint AddBreakpoint(int maximumWidth, Action<bool> applied)
    {
        var breakpoint = Adw.Breakpoint.New(Adw.BreakpointCondition.NewLength(Adw.BreakpointConditionLengthType.MaxWidth, maximumWidth, Adw.LengthUnit.Px));
        _subscriptions.Add(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnApply += h, h => breakpoint.OnApply -= h, (_, _) =>
        {
            applied(true);
            ApplyWidthClass();
        }));
        _subscriptions.Add(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnUnapply += h, h => breakpoint.OnUnapply -= h, (_, _) =>
        {
            applied(false);
            ApplyWidthClass();
        }));
        _root.AddBreakpoint(breakpoint);
        return breakpoint;
    }

    private void ApplyWidthClass()
    {
        var widthClass = _compactApplied ? ShellWidthClass.Compact : _mediumApplied ? ShellWidthClass.Medium : ShellWidthClass.Wide;
        if (_hasWidthClass && widthClass == WidthClass) return;
        _hasWidthClass = true;
        WidthClass = widthClass;
        var wide = widthClass == ShellWidthClass.Wide;
        _handle.Root.SetVisible(wide);
        _split.SetCollapsed(!wide);
        _split.SetShowSidebar(wide && _preferences.IsNavigationPaneOpened);
        StateChanged?.Invoke();
    }
}
