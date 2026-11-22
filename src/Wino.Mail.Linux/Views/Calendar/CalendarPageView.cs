using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Calendar;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Calendar mode content (Windows CalendarPage, macOS CalendarPageViewController): the period row above the surface,
/// the 320 px details zone at the trailing edge (floating over the surface on a narrow page) and a cover slot over the
/// whole page. While it is shown the page is the rendering frame, so event details and the composer land here.
/// Later packages extend it through the <c>Build…</c> hooks and their own partial files; none edits this file.
/// </summary>
internal sealed partial class CalendarPageView : WinoView<CalendarPageViewModel>, IRenderingFrameHost
{
    private const int DetailsWidth = 320;
    private const int CompactBelowWidth = 520;
    private const int FloatingDetailsBelowWidth = DetailsWidth + 560;

    private readonly GtkNavigationService _navigation;
    private readonly CalendarAppShellViewModel _shell;
    private readonly IDateContextProvider _dateContext;
    private readonly IContextMenuItemService _contextMenus;
    private readonly Gtk.Overlay _root = Gtk.Overlay.New();
    private readonly Adw.BreakpointBin _breakpoints = Adw.BreakpointBin.New();
    private readonly Gtk.Overlay _body = Gtk.Overlay.New();
    private readonly Gtk.Box _columns = Gtk.Box.New(Gtk.Orientation.Horizontal, 7);
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Adw.Bin _surfaceHost = Adw.Bin.New();
    private readonly Adw.Bin _detailsZone = Adw.Bin.New();
    private readonly CalendarToolbar _toolbar = new();
    private readonly CalendarReadinessView _readiness = new();
    private readonly List<Action> _activated = [];
    private readonly List<Func<Task>> _deactivating = [];
    private IDisposable? _hostOverride;

    public CalendarPageView(CalendarPageViewModel viewModel, WinoViewServices services, GtkNavigationService navigation,
        CalendarAppShellViewModel shell, IDateContextProvider dateContext, IContextMenuItemService contextMenus)
        : base(viewModel, services)
    {
        _contextMenus = contextMenus;
        _navigation = navigation;
        _shell = shell;
        _dateContext = dateContext;

        _stack.SetHexpand(true);
        _stack.SetVexpand(true);
        _stack.AddNamed(_surfaceHost, "surface");
        _stack.AddNamed(_readiness.Root, "readiness");
        _stack.SetVisibleChildName("surface");

        _detailsZone.SetSizeRequest(DetailsWidth, -1);
        _detailsZone.SetHexpand(false);
        _detailsZone.SetVisible(false);
        _detailsZone.AddCssClass("view");

        _columns.Append(_stack);
        _columns.Append(_detailsZone);
        _body.SetChild(_columns);
        _body.SetVexpand(true);

        var page = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        page.Append(_toolbar.Root);
        BuildDueTasks(page);
        page.Append(_body);
        _breakpoints.SetSizeRequest(360, 300);
        _breakpoints.SetChild(page);
        AddBreakpoint(CompactBelowWidth, compact => _toolbar.SetCompact(compact));
        AddBreakpoint(FloatingDetailsBelowWidth, SetDetailsFloating);
        _root.SetChild(_breakpoints);
        Bindings.Own(_toolbar);

        BuildSurface(_surfaceHost);
        BuildInteraction();
        BuildQuickEvent();
        BuildDrag();
        BuildSearch();
        BuildDebug();
    }

    public override Gtk.Widget Root => _root;

    /// <summary>Set by the surface package: moves the rail by whole periods; false while it shows no period.</summary>
    internal Func<int, bool>? SurfaceNavigator { get; set; }

    internal CalendarToolbar Toolbar => _toolbar;

    internal CalendarAppShellViewModel Shell => _shell;

    partial void BuildSurface(Adw.Bin host);
    partial void BuildDueTasks(Gtk.Box page);
    partial void BuildInteraction();
    partial void BuildQuickEvent();
    partial void BuildDrag();
    partial void BuildSearch();
    partial void BuildDebug();

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _hostOverride = _navigation.OverrideRenderingHost(this);
        Bindings.Own(_hostOverride);
        await base.InitializeAsync(mode, parameter);
        if (IsReleased) return;
        _pendingTarget = (parameter as CalendarPageNavigationArgs)?.PendingTarget;

        Bind.Bind(nameof(CalendarPageViewModel.VisibleDateRangeText), static vm => vm.VisibleDateRangeText, text => _toolbar.RangeText = text ?? string.Empty);
        Bindings.Own(new PropertyBinding<IStatePersistanceService, CalendarDisplayType>(ViewModel.StatePersistanceService,
            nameof(IStatePersistanceService.CalendarDisplayType), static state => state.CalendarDisplayType, type => _toolbar.SelectedType = type, Dispatcher, ReportError));
        var readiness = Bind.For(ViewModel.Readiness);
        readiness.BindAny(static r => (r.IsBlocked, r.Title, r.Message, r.IsProgressVisible), ApplyReadiness);
        _readiness.Bind(readiness);

        _toolbar.PreviousRequested += (_, _) => NavigatePeriods(-1);
        _toolbar.NextRequested += (_, _) => NavigatePeriods(1);
        _toolbar.TodayRequested += (_, _) => RequestToday();
        _toolbar.DisplayTypeRequested += (_, type) => ViewModel.StatePersistanceService.CalendarDisplayType = type;

        foreach (var action in _activated) action();

        Messenger.Send(CalendarPageActivation.CreateLoadMessage(parameter, ViewModel.StatePersistanceService.CalendarDisplayType, _dateContext.GetToday()));
    }

    protected override async Task DeactivateAsync()
    {
        _hostOverride?.Dispose();
        for (var index = _deactivating.Count - 1; index >= 0; index--)
        {
            try { await _deactivating[index](); }
            catch (Exception exception) { ReportError(exception); }
        }
        await ClearAsync();
        _toolbar.Dispose();
        await base.DeactivateAsync();
    }

    internal void NavigatePeriods(int direction)
    {
        if (SurfaceNavigator?.Invoke(direction) == true) return;
        var command = direction < 0 ? _shell.PreviousDateRangeCommand : _shell.NextDateRangeCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    internal void RequestToday()
    {
        if (_shell.TodayClickedCommand.CanExecute(null)) _shell.TodayClickedCommand.Execute(null);
    }

    private void ApplyReadiness((bool IsBlocked, string Title, string Message, bool IsProgressVisible) state)
    {
        _stack.SetVisibleChildName(state.IsBlocked ? "readiness" : "surface");
        _readiness.Update(state.Title, state.Message, state.IsProgressVisible);
        _toolbar.Root.SetSensitive(!state.IsBlocked);
    }

    private void AddBreakpoint(int belowWidth, Action<bool> apply)
    {
        var breakpoint = Adw.Breakpoint.New(Adw.BreakpointCondition.NewLength(Adw.BreakpointConditionLengthType.MaxWidth, belowWidth - 1, Adw.LengthUnit.Px));
        Bindings.Own(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnApply += h, h => breakpoint.OnApply -= h, (_, _) => apply(true)));
        Bindings.Own(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnUnapply += h, h => breakpoint.OnUnapply -= h, (_, _) => apply(false)));
        _breakpoints.AddBreakpoint(breakpoint);
    }
}
