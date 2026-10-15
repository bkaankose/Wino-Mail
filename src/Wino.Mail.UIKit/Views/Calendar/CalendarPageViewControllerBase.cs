using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Messages;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.UIKit.Calendar;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Messaging.Client.Calendar;
using Wino.Presentation.Bindings;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// What the iPhone and iPad calendar pages share (the Mac CalendarPageViewController without its layout): the period
/// surface bound to the shared <see cref="CalendarPageViewModel"/>, the readiness state, tile selection driving the event
/// details route, drag to move and resize, the shared context menu, the quick event and the debug bridge commands.
/// The idiom pages build the layout, the date navigation and where details and the composer appear.
/// </summary>
public abstract partial class CalendarPageViewControllerBase : WinoViewController<CalendarPageViewModel>, IRenderingFrameHost
{
    private readonly IContextMenuItemService _contextMenus;
    private readonly IMailDialogService _dialogs;
    private UIEditMenuInteraction? _editMenu;
    private EditMenuDelegate? _editMenuDelegate;
    private CalendarItemViewModel? _menuItem;
    private QuickEventViewController? _quickEvent;
    private CalendarItemViewModel? _selected;

    protected CalendarPageViewControllerBase(CalendarPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
        CalendarAppShellViewModel shell, IContextMenuItemService contextMenus, IDateContextProvider dateContext, IMailDialogService dialogs,
        IServiceProvider services)
        : base(viewModel, dispatcher, logger)
    {
        Shell = shell;
        _contextMenus = contextMenus;
        DateContext = dateContext;
        _dialogs = dialogs;
        Services = services;
        Surface = new WinoCalendarSurfaceView
        {
            TileFactory = (item, date) => CalendarTileMapper.Map(item, date, ViewModel.CurrentSettings),
            Today = () => DateContext.GetToday(),
            CanDragItem = CalendarTileMapper.CanDrag
        };
        Readiness = new CalendarReadinessView { Hidden = true };
    }

    protected CalendarAppShellViewModel Shell { get; }
    protected IDateContextProvider DateContext { get; }
    protected IServiceProvider Services { get; }
    protected WinoCalendarSurfaceView Surface { get; }
    protected CalendarReadinessView Readiness { get; }

    /// <summary>True once the page was released; late callbacks do nothing.</summary>
    protected bool Released { get; private set; }

    protected UIKitNavigationService? Navigation => Services.GetService<UIKitNavigationService>();

    /// <summary>The event details page on screen (pushed on iPhone, in the reader column on iPad), if any.</summary>
    protected abstract EventDetailsPageViewController? CurrentDetails { get; }

    /// <summary>
    /// Where the quick event appears for an empty slot: the anchor view and rectangle for a popover, or null view for
    /// a sheet. Null result: no quick event for this slot (an iPhone tap; only a long press creates).
    /// </summary>
    protected abstract (UIView? View, CGRect Rect)? QuickEventAnchor(CalendarSlotClickedEventArgs args);

    /// <summary>The details item changed to null (a slot tap or the page leaving): idiom hosts hide their details.</summary>
    protected virtual Task DetailsClearedAsync() => Task.CompletedTask;

    /// <summary>
    /// The event composer, routed to the rendering frame: the idiom page shows it (iPhone: a full-screen sheet; iPad: in
    /// place over the surface) and releases it when it closes. Other rendering-frame pages (event details) go to the
    /// shell's frame: pushed on iPhone, the reader column on iPad.
    /// </summary>
    bool IRenderingFrameHost.TryShowInFrame(UIViewController page)
        => page is CalendarEventComposePageViewController composer && !Released && ShowComposer(composer);

    /// <summary>Shows the event composer this page now owns. False when it cannot (released, not on screen).</summary>
    protected abstract bool ShowComposer(CalendarEventComposePageViewController composer);

    /// <summary>Wires the surface and readiness view into <paramref name="zoneContent"/>. Call from LoadView.</summary>
    protected void PlaceSurface(UIView zoneContent)
    {
        Surface.ItemDragRefused += SurfaceItemDragRefused;
        Surface.ItemMoveRequested += SurfaceItemMoveRequested;
        Surface.ItemResizeRequested += SurfaceItemResizeRequested;
        Surface.ItemClicked += SurfaceItemClicked;
        Surface.ItemDoubleClicked += SurfaceItemDoubleClicked;
        Surface.ItemRightClicked += SurfaceItemRightClicked;
        Surface.SlotClicked += SurfaceSlotClicked;
        Wino.Presentation.UIKit.WinoLayout.Fill(Surface, zoneContent);
        Wino.Presentation.UIKit.WinoLayout.Fill(Readiness, zoneContent);
        _editMenuDelegate = new EditMenuDelegate(this);
        _editMenu = new UIEditMenuInteraction(_editMenuDelegate);
        Surface.AddInteraction(_editMenu);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        // The event composer is routed to the rendering frame; this page shows it (Mac AttachRenderingHost).
        Navigation?.AttachRenderingHost(this);
        ViewModel.OnNavigatedTo(mode, parameter ?? NoParameter.Instance);
        BindSurface();

        // Windows CalendarPage.OnNavigatedTo: resolve the anchor date from the navigation args and load (shared rule).
        WeakReferenceMessenger.Default.Send(CalendarPageActivation.CreateLoadMessage(parameter,
            InitialDisplayType(ViewModel.StatePersistanceService.CalendarDisplayType), DateContext.GetToday()));

#if DEBUG
        RegisterDebugCommands();
        // "page CalendarPage" in the debug bridge can land here before the calendar shell loaded account calendars;
        // run its activation like Windows does (Mac twin).
        if (parameter is null && !Shell.AccountCalendarStateService.GroupedAccountCalendars.Any())
        {
            ViewModel.StatePersistanceService.ApplicationMode = WinoApplicationMode.Calendar;
            Shell.Dispatcher ??= Dispatcher;
            Shell.AccountCalendarStateService.Dispatcher ??= Dispatcher;
            Shell.OnNavigatedTo(NavigationMode.New, new ShellModeActivationContext { IsInitialActivation = true });
        }
#endif
        return Task.CompletedTask;
    }

    /// <summary>The display type the page starts with; the iPhone maps the desktop-only Work week to Week.</summary>
    protected virtual CalendarDisplayType InitialDisplayType(CalendarDisplayType persisted) => persisted;

    private void BindSurface()
    {
        Bind(nameof(ViewModel.CurrentVisibleRange), vm => vm.CurrentVisibleRange, range => { Surface.VisibleRange = range; VisibleRangeChanged(range); });
        Bind(nameof(ViewModel.CurrentSettings), vm => vm.CurrentSettings, settings => { Surface.Settings = settings; SettingsChanged(settings); });
        Bind(nameof(ViewModel.CalendarItems), vm => vm.CalendarItems, items => { Surface.Items = items; ItemsChanged(); });
        Bind(nameof(ViewModel.IsCalendarEnabled), vm => vm.IsCalendarEnabled, enabled => Surface.Alpha = enabled ? 1 : 0.6f);
        Bind(nameof(ViewModel.DisplayDetailsCalendarItemViewModel), vm => vm.DisplayDetailsCalendarItemViewModel, DetailsItemChanged);
        // The quick event's range stays highlighted while it is open (Windows SelectedDateTime/SelectedEndDateTime).
        Bind(nameof(ViewModel.QuickEventSelectionEnd), vm => vm.QuickEventSelectionEnd, _ => ApplySelectionRange());
        Bind(nameof(ViewModel.SelectedStartTimeString), vm => vm.SelectedStartTimeString, _ => ApplySelectionRange());
        Bindings.Own(new PropertyBinding<IStatePersistanceService, CalendarDisplayType>(ViewModel.StatePersistanceService,
            nameof(IStatePersistanceService.CalendarDisplayType), state => state.CalendarDisplayType, DisplayTypeChanged, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, bool>(ViewModel.Readiness, nameof(ModeReadinessViewModel.IsBlocked),
            readiness => readiness.IsBlocked, _ => ApplyReadiness(), Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, string>(ViewModel.Readiness, nameof(ModeReadinessViewModel.Message),
            readiness => readiness.Message, _ => ApplyReadiness(), Dispatcher, ReportError));
        Bind(nameof(ViewModel.VisibleDateRangeText), vm => vm.VisibleDateRangeText, RangeTextChanged);
    }

    /// <summary>Hooks for the idiom layouts (header pager, agenda, toolbar).</summary>
    protected virtual void VisibleRangeChanged(VisibleDateRange? range) { }
    protected virtual void SettingsChanged(CalendarSettings? settings) { }
    protected virtual void ItemsChanged() { }
    protected virtual void DisplayTypeChanged(CalendarDisplayType type) { }
    protected virtual void RangeTextChanged(string? text) { }
    protected virtual void ReadinessChanged(bool blocked) { }

    /// <summary>Asks the shared state for another display type; the shell VM reloads the range.</summary>
    protected void RequestDisplayType(CalendarDisplayType type)
    {
        if (ViewModel.StatePersistanceService.CalendarDisplayType != type) ViewModel.StatePersistanceService.CalendarDisplayType = type;
    }

    protected void GoPrevious() => Shell.PreviousDateRangeCommand.Execute(null);
    protected void GoNext() => Shell.NextDateRangeCommand.Execute(null);
    protected void GoToday() => Shell.TodayClickedCommand.Execute(null);
    protected void NewEvent() => Shell.NewEventCommand.Execute(null);

    /// <summary>A day picked in a date picker or pager (Windows ShellCalendarView DateClicked).</summary>
    protected void GoToDate(DateOnly date)
    {
        var args = new CalendarViewDayClickedEventArgs(date.ToDateTime(TimeOnly.MinValue));
        if (Shell.DateClickedCommand.CanExecute(args)) Shell.DateClickedCommand.Execute(args);
    }

    private void ApplySelectionRange()
    {
        CalendarSelectionRange? range = null;
        bool month = ViewModel.CurrentVisibleRange?.DisplayType == CalendarDisplayType.Month;
        if (ViewModel.SelectedQuickEventDate is { } date && ViewModel.QuickEventSelectionEnd is { } end && (month || !ViewModel.IsAllDay))
        {
            var start = ViewModel.IsAllDay ? date.Date
                : ViewModel.CurrentSettings?.GetTimeSpan(ViewModel.SelectedStartTimeString) is TimeSpan time ? date.Date.Add(time) : date;
            if (end > start) range = new CalendarSelectionRange(start, end);
        }
        Surface.SelectionRange = range;
    }

    private void ApplyReadiness()
    {
        var readiness = ViewModel.Readiness;
        Readiness.Hidden = !readiness.IsBlocked;
        Surface.Hidden = readiness.IsBlocked;
        if (readiness.IsBlocked) Readiness.Update(readiness.Title, readiness.Message, readiness.IsProgressVisible);
        ReadinessChanged(readiness.IsBlocked);
    }

    private void DetailsItemChanged(CalendarItemViewModel? item)
    {
        if (!ReferenceEquals(_selected, item))
        {
            if (_selected is not null) _selected.IsSelected = false;
            _selected = item;
            if (item is not null) item.IsSelected = true;
            Surface.RefreshTiles();
        }
        if (item is null) Observe(DetailsClearedAsync());
        else ViewModel.NavigateEventDetailsCommand.Execute(null);
    }

    /// <summary>
    /// Clears the details item without routing (the iPhone details page was popped): the next tap on the same tile must
    /// change the value again.
    /// </summary>
    protected void ResetDetailsItem()
    {
        if (ViewModel.DisplayDetailsCalendarItemViewModel is not null) ViewModel.DisplayDetailsCalendarItemViewModel = null;
    }

    // ------------------------------------------------------------ surface interaction

    /// <summary>A tile tap: the shared message sets the details item, which routes the details page.</summary>
    protected void SelectItem(CalendarItemViewModel item)
    {
        CloseQuickEvent();
        // Tapping the item whose details just closed must route again.
        if (ReferenceEquals(ViewModel.DisplayDetailsCalendarItemViewModel, item) && CurrentDetails is null) ResetDetailsItem();
        WeakReferenceMessenger.Default.Send(new CalendarItemTappedMessage(item));
    }

    private void SurfaceItemClicked(object? sender, CalendarItemClickedEventArgs args)
    {
        if (args.Item is CalendarItemViewModel item) SelectItem(item);
    }

    private void SurfaceItemDoubleClicked(object? sender, CalendarItemClickedEventArgs args)
    {
        CloseQuickEvent();
        if (args.Item is CalendarItemViewModel item) WeakReferenceMessenger.Default.Send(new CalendarItemDoubleTappedMessage(item));
    }

    /// <summary>A long press released without moving: the shared context menu at the tile (Mac right click).</summary>
    private void SurfaceItemRightClicked(object? sender, CalendarItemClickedEventArgs args)
    {
        CloseQuickEvent();
        if (args.Item is not CalendarItemViewModel item) return;
        var frame = args.View.ConvertRectToView(args.View.Bounds, Surface);
        ShowContextMenu(item, new CGPoint(frame.GetMidX(), frame.GetMidY()));
    }

    /// <summary>Shows the shared context menu of <paramref name="item"/> at <paramref name="point"/> in the surface.</summary>
    protected void ShowContextMenu(CalendarItemViewModel item, CGPoint point)
    {
        if (_editMenu is null) return;
        _menuItem = item;
        _editMenu.PresentEditMenu(UIEditMenuConfiguration.Create(null, point));
    }

    /// <summary>The menu of a list row (the agenda): a UIMenu for a context menu interaction.</summary>
    protected UIMenu ContextMenu(CalendarItemViewModel item) => CalendarContextMenuLabels.CreateMenu(_contextMenus, item);

    private sealed class EditMenuDelegate(CalendarPageViewControllerBase owner) : UIEditMenuInteractionDelegate
    {
        public override UIMenu? GetMenu(UIEditMenuInteraction interaction, UIEditMenuConfiguration configuration, UIMenuElement[] suggestedActions)
            => owner._menuItem is { } item ? CalendarContextMenuLabels.CreateMenu(owner._contextMenus, item) : null;
    }

    /// <summary>Windows refuses the drag of a locked or read-only event; the Mac and iOS also say why.</summary>
    private void SurfaceItemDragRefused(object? sender, CalendarItemClickedEventArgs args)
    {
        CloseQuickEvent();
        switch (CalendarTileMapper.DragRefusal(args.Item))
        {
            case CalendarDragRefusal.ReadOnlyCalendar:
                _dialogs.ShowReadOnlyCalendarMessage();
                break;
            case CalendarDragRefusal.NotAllowed:
                _dialogs.InfoBarMessage(Translator.CalendarDragDropMoveNotAllowedTitle, Translator.CalendarDragDropMoveNotAllowedMessage, InfoBarMessageType.Warning);
                break;
        }
    }

    private void SurfaceItemMoveRequested(object? sender, CalendarItemMoveRequestedEventArgs args)
    {
        CloseQuickEvent();
        if (args.Item is CalendarItemViewModel item) Observe(ChangeTimesAsync(ViewModel.MoveCalendarItemAsync(item, args.Start)));
    }

    private void SurfaceItemResizeRequested(object? sender, CalendarItemResizeRequestedEventArgs args)
    {
        CloseQuickEvent();
        if (args.Item is CalendarItemViewModel item) Observe(ChangeTimesAsync(ViewModel.ResizeCalendarItemAsync(item, args.End)));
    }

    /// <summary>The ViewModel changes the item in place; lay the tiles out again once it has.</summary>
    private async Task ChangeTimesAsync(Task change)
    {
        try { await change; }
        finally
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (Released) return;
                Surface.ReloadLayout();
                Surface.RefreshTiles();
                ItemsChanged();
            });
        }
    }

    private void SurfaceSlotClicked(object? sender, CalendarSlotClickedEventArgs args)
    {
        if (ViewModel.DisplayDetailsCalendarItemViewModel is not null && CurrentDetails is not null)
        {
            ViewModel.DisplayDetailsCalendarItemViewModel = null;
            return;
        }
        if (!ViewModel.Readiness.IsReady) return;
        if (QuickEventAnchor(args) is not { } anchor) return;
        OpenQuickEvent(args.Start, args.End, anchor.View, anchor.Rect);
    }

    /// <summary>Selects the quick event range and opens the quick event (popover or sheet).</summary>
    protected void OpenQuickEvent(DateTime start, DateTime? draggedEnd, UIView? anchorView, CGRect anchorRect)
    {
        bool isAllDay = ViewModel.CurrentVisibleRange?.DisplayType == CalendarDisplayType.Month;
        // A drag across slots carries its own end (Windows EmptySlotTapped EndDate).
        var end = draggedEnd is { } dragged && dragged > start ? dragged : isAllDay ? start.Date.AddDays(1) : start.AddMinutes(30);
        ViewModel.SelectQuickEventRange(start, end, isAllDay);
        CloseQuickEvent();
        var quick = new QuickEventViewController(ViewModel, Dispatcher, ReportError);
        quick.Closed += (_, _) => { if (ReferenceEquals(_quickEvent, quick)) _quickEvent = null; };
        _quickEvent = quick;
        quick.Present(this, anchorView, anchorRect);
    }

    protected void CloseQuickEvent()
    {
        var quick = _quickEvent;
        _quickEvent = null;
        quick?.Dismiss();
    }

    protected QuickEventViewController? OpenQuickEventController => _quickEvent;

    // ------------------------------------------------------------ lifecycle

    protected override async Task DeactivateAsync()
    {
        Released = true;
        Navigation?.DetachRenderingHost(this);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            CloseQuickEvent();
            ReleaseIdiom();
        });
        await ReleaseHostedPagesAsync();
        if (_selected is not null) { _selected.IsSelected = false; _selected = null; }
        ViewModel.OnNavigatedFrom(NavigationMode.New, NoParameter.Instance);
    }

    /// <summary>Idiom clean-up on the main thread when the page is released (search, sidebar, column widths).</summary>
    protected virtual void ReleaseIdiom() { }

    /// <summary>Releases pages the idiom page hosts itself (the iPad composer in place).</summary>
    protected virtual Task ReleaseHostedPagesAsync() => Task.CompletedTask;

    protected async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Released = true;
            _quickEvent?.Dismiss();
            _quickEvent = null;
            if (_editMenu is not null) { Surface.RemoveInteraction(_editMenu); _editMenu = null; }
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// The shared ViewModel contract declares a non-null navigation parameter that this page never reads; this stands in
    /// for "no parameter" without a null-forgiving operator (same approach as the phone and pad mail pages).
    /// </summary>
    protected sealed class NoParameter
    {
        public static readonly NoParameter Instance = new();
        private NoParameter() { }
    }
}
