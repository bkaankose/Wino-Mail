using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Messages;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.AppKit.Calendar;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Messaging.Client.Calendar;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// Calendar mode content (Windows CalendarPage): the title bar content row (previous/next, range
/// text, Today and the type selector) above the period surface in a 7pt Wino zone, plus the
/// 320pt event details zone on the right that hosts <see cref="EventDetailsPageViewController"/>.
/// The event composer, also routed to the rendering frame, opens as a sheet.
/// </summary>
public sealed class CalendarPageViewController : WinoViewController<CalendarPageViewModel>, IRenderingFrameHost
{
    private const double DetailsWidth = 320;
    private readonly AppKitNavigationService _navigation;
    private readonly CalendarAppShellViewModel _shell;
    private readonly IContextMenuItemService _contextMenus;
    private readonly IDateContextProvider _dateContext;
    private readonly IMailDialogService _dialogs;
    private readonly SemaphoreSlim _paneGate = new(1, 1);
    private CalendarToolbarView _toolbar = null!;
    private WinoZoneView _zone = null!;
    private WinoCalendarSurfaceView _surface = null!;
    private CalendarReadinessView _readiness = null!;
    private WinoZoneView _detailsZone = null!;
    private NSView _detailsContent = null!;
    private NSLayoutConstraint _detailsWidth = null!;
    private NSViewController? _detailsChild;
    private CalendarEventComposePageViewController? _composer;
    private NSWindow? _composerSheet;
    private QuickEventPopover? _quickEvent;
    private CalendarItemViewModel? _selected;
    private bool _released;

    public CalendarPageViewController(CalendarPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
        AppKitNavigationService navigation, CalendarAppShellViewModel shell, IContextMenuItemService contextMenus, IDateContextProvider dateContext,
        IMailDialogService dialogs)
        : base(viewModel, dispatcher, logger)
    {
        _dialogs = dialogs;
        _navigation = navigation;
        _shell = shell;
        _contextMenus = contextMenus;
        _dateContext = dateContext;
    }

    public override void LoadView()
    {
        var root = new WinoSurfaceView { Fill = null, AccessibilityElement = false };

        _toolbar = new CalendarToolbarView();
        _toolbar.PreviousRequested += (_, _) => _shell.PreviousDateRangeCommand.Execute(null);
        _toolbar.NextRequested += (_, _) => _shell.NextDateRangeCommand.Execute(null);
        _toolbar.TodayRequested += (_, _) => _shell.TodayClickedCommand.Execute(null);
        _toolbar.DisplayTypeRequested += (_, type) => ViewModel.StatePersistanceService.CalendarDisplayType = type;

        _zone = new WinoZoneView();
        ((WinoSurfaceView)_zone.ContentView).CornerRadius = 7;
        _surface = new WinoCalendarSurfaceView
        {
            TileFactory = (item, date) => CalendarTileMapper.Map(item, date, ViewModel.CurrentSettings),
            Today = () => _dateContext.GetToday(),
            CanDragItem = CalendarTileMapper.CanDrag
        };
        _surface.ItemDragRefused += SurfaceItemDragRefused;
        _surface.ItemMoveRequested += SurfaceItemMoveRequested;
        _surface.ItemResizeRequested += SurfaceItemResizeRequested;
        _surface.ItemClicked += SurfaceItemClicked;
        _surface.ItemDoubleClicked += SurfaceItemDoubleClicked;
        _surface.ItemRightClicked += SurfaceItemRightClicked;
        _surface.SlotClicked += SurfaceSlotClicked;
        WinoLayout.Fill(_surface, _zone.ContentView);
        _readiness = new CalendarReadinessView { Hidden = true };
        WinoLayout.Fill(_readiness, _zone.ContentView);

        _detailsZone = new WinoZoneView { Hidden = true };
        ((WinoSurfaceView)_detailsZone.ContentView).CornerRadius = 7;
        _detailsContent = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_detailsContent, _detailsZone.ContentView);
        _detailsWidth = _detailsZone.WidthAnchor.ConstraintEqualTo((nfloat)DetailsWidth);
        _detailsWidth.Active = true;

        root.AddSubview(_toolbar);
        root.AddSubview(_zone);
        root.AddSubview(_detailsZone);
        var zoneTrailing = _zone.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -7);
        zoneTrailing.Priority = 700;
        NSLayoutConstraint.ActivateConstraints(
        [
            _toolbar.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            _toolbar.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _toolbar.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _zone.TopAnchor.ConstraintEqualTo(_toolbar.BottomAnchor),
            _zone.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 4),
            _zone.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -7),
            zoneTrailing,
            _detailsZone.TopAnchor.ConstraintEqualTo(_zone.TopAnchor),
            _detailsZone.BottomAnchor.ConstraintEqualTo(_zone.BottomAnchor),
            _detailsZone.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -7)
        ]);
        _detailsLeading = _detailsZone.LeadingAnchor.ConstraintEqualTo(_zone.TrailingAnchor, 7);
        View = root;
    }

    private NSLayoutConstraint _detailsLeading = null!;

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _navigation.AttachRenderingHost(this);
        ViewModel.OnNavigatedTo(mode, parameter!);
        BindAll();

        // Windows CalendarPage.OnNavigatedTo: resolve the anchor date from the navigation args and load.
        var anchorDate = _dateContext.GetToday();
        CalendarItemTarget? pendingTarget = null;
        bool forceReload = false;
        if (parameter is CalendarPageNavigationArgs args)
        {
            if (!args.RequestDefaultNavigation) anchorDate = DateOnly.FromDateTime(args.NavigationDate.Date);
            pendingTarget = args.PendingTarget;
            forceReload = args.ForceReload;
        }
        var request = new CalendarDisplayRequest(ViewModel.StatePersistanceService.CalendarDisplayType, anchorDate);
        WeakReferenceMessenger.Default.Send(new LoadCalendarMessage(request, forceReload, pendingTarget));

#if DEBUG
        RegisterDebugCommands();
        // Until the shell switches modes, "page CalendarPage" in the debug bridge lands here without
        // the calendar shell having loaded account calendars; run its activation like Windows does.
        if (parameter is null && !_shell.AccountCalendarStateService.GroupedAccountCalendars.Any())
        {
            ViewModel.StatePersistanceService.ApplicationMode = WinoApplicationMode.Calendar;
            _shell.Dispatcher ??= Dispatcher;
            _shell.AccountCalendarStateService.Dispatcher ??= Dispatcher;
            _shell.OnNavigatedTo(NavigationMode.New, new ShellModeActivationContext { IsInitialActivation = true });
        }
#endif
        return Task.CompletedTask;
    }

    private void BindAll()
    {
        Bind(nameof(ViewModel.VisibleDateRangeText), vm => vm.VisibleDateRangeText, text => _toolbar.RangeText = text);
        Bind(nameof(ViewModel.CurrentVisibleRange), vm => vm.CurrentVisibleRange, range => _surface.VisibleRange = range);
        Bind(nameof(ViewModel.CurrentSettings), vm => vm.CurrentSettings, settings => _surface.Settings = settings);
        Bind(nameof(ViewModel.CalendarItems), vm => vm.CalendarItems, items => _surface.Items = items);
        Bind(nameof(ViewModel.IsCalendarEnabled), vm => vm.IsCalendarEnabled, enabled => _surface.AlphaValue = enabled ? 1 : (nfloat)0.6);
        Bind(nameof(ViewModel.DisplayDetailsCalendarItemViewModel), vm => vm.DisplayDetailsCalendarItemViewModel, DetailsItemChanged);
        Bindings.Own(new PropertyBinding<IStatePersistanceService, CalendarDisplayType>(ViewModel.StatePersistanceService,
            nameof(IStatePersistanceService.CalendarDisplayType), state => state.CalendarDisplayType, type => _toolbar.SelectedType = type, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, bool>(ViewModel.Readiness, nameof(ModeReadinessViewModel.IsBlocked),
            readiness => readiness.IsBlocked, _ => ApplyReadiness(), Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, string>(ViewModel.Readiness, nameof(ModeReadinessViewModel.Message),
            readiness => readiness.Message, _ => ApplyReadiness(), Dispatcher, ReportError));
    }

    private void Bind<TValue>(string property, Func<CalendarPageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<CalendarPageViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

    private void ApplyReadiness()
    {
        var readiness = ViewModel.Readiness;
        _readiness.Hidden = !readiness.IsBlocked;
        _surface.Hidden = readiness.IsBlocked;
        if (readiness.IsBlocked) _readiness.Update(readiness.Title, readiness.Message, readiness.IsProgressVisible);
    }

    private void DetailsItemChanged(CalendarItemViewModel? item)
    {
        if (!ReferenceEquals(_selected, item))
        {
            if (_selected is not null) _selected.IsSelected = false;
            _selected = item;
            if (item is not null) item.IsSelected = true;
            _surface.RefreshTiles();
        }
        if (item is null) Observe(ClearAsync());
        else ViewModel.NavigateEventDetailsCommand.Execute(null);
    }

    // ------------------------------------------------------------ surface interaction

    private void SurfaceItemClicked(object? sender, CalendarItemClickedEventArgs args)
    {
        CloseQuickEvent();
        if (args.Item is CalendarItemViewModel item) WeakReferenceMessenger.Default.Send(new CalendarItemTappedMessage(item));
    }

    private void SurfaceItemDoubleClicked(object? sender, CalendarItemClickedEventArgs args)
    {
        CloseQuickEvent();
        if (args.Item is CalendarItemViewModel item) WeakReferenceMessenger.Default.Send(new CalendarItemDoubleTappedMessage(item));
    }

    private void SurfaceItemRightClicked(object? sender, CalendarItemClickedEventArgs args)
    {
        CloseQuickEvent();
        if (args.Item is not CalendarItemViewModel item) return;
        WeakReferenceMessenger.Default.Send(new CalendarItemRightTappedMessage(item));
        var menu = new NSMenu { AutoEnablesItems = false };
        foreach (var entry in _contextMenus.GetCalendarItemContextMenuItems(item.CalendarItem)) menu.AddItem(MenuItem(entry, item));
        EventDetailsWindow.AppendMenuItem(menu, item);
        if (args.NativeEvent is { } native) NSMenu.PopUpContextMenu(menu, native, args.View);
    }

    private static NSMenuItem MenuItem(CalendarContextMenuItem entry, CalendarItemViewModel item)
    {
        var menuItem = new NSMenuItem(CalendarContextMenuLabels.Label(entry.Action)) { Enabled = CalendarContextMenuLabels.IsEnabled(entry, item) };
        var glyph = CalendarContextMenuLabels.Glyph(entry.Action);
        if (glyph != WinoIconGlyph.None) menuItem.Image = WinoIcons.Image(glyph, 14);
        if (entry.HasChildren)
        {
            var submenu = new NSMenu { AutoEnablesItems = false };
            foreach (var child in entry.Children) submenu.AddItem(MenuItem(child, item));
            menuItem.Submenu = submenu;
        }
        else
        {
            var action = entry.Action;
            menuItem.Activated += (_, _) => WeakReferenceMessenger.Default.Send(new CalendarItemContextActionRequestedMessage(item, action));
        }
        return menuItem;
    }

    // ------------------------------------------------------------ drag to move / resize

    /// <summary>Windows refuses the drag of a locked or read-only event; the Mac also says why.</summary>
    private void SurfaceItemDragRefused(object? sender, CalendarItemClickedEventArgs args)
    {
        CloseQuickEvent();
        switch (CalendarTileMapper.DragRefusal(args.Item))
        {
            case CalendarDragRefusal.ReadOnlyCalendar:
                _dialogs.ShowReadOnlyCalendarMessage();
                break;
            case CalendarDragRefusal.NotAllowed:
                _dialogs.InfoBarMessage(Wino.Core.Domain.Translator.CalendarDragDropMoveNotAllowedTitle,
                    Wino.Core.Domain.Translator.CalendarDragDropMoveNotAllowedMessage, InfoBarMessageType.Warning);
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
                if (_released) return;
                _surface.ReloadLayout();
                _surface.RefreshTiles();
            });
        }
    }

    private void SurfaceSlotClicked(object? sender, CalendarSlotClickedEventArgs args)
    {
        if (ViewModel.DisplayDetailsCalendarItemViewModel is not null)
        {
            ViewModel.DisplayDetailsCalendarItemViewModel = null;
            return;
        }
        if (!ViewModel.Readiness.IsReady) return;
        bool isAllDay = ViewModel.CurrentVisibleRange?.DisplayType == CalendarDisplayType.Month;
        ViewModel.SelectQuickEventRange(args.Start, isAllDay ? args.Start.Date.AddDays(1) : args.Start.AddMinutes(30), isAllDay);
        CloseQuickEvent();
        _quickEvent = new QuickEventPopover(ViewModel, Dispatcher, ReportError);
        _quickEvent.Closed += (_, _) => { _quickEvent = null; };
        var edge = isAllDay ? NSRectEdge.MaxYEdge : NSRectEdge.MaxXEdge;
        if (!isAllDay && args.Anchor.GetMaxX() + 340 > _surface.Bounds.Width) edge = NSRectEdge.MinXEdge;
        _quickEvent.Present(args.Anchor, _surface, edge);
    }

    private void CloseQuickEvent()
    {
        _quickEvent?.Close();
        _quickEvent = null;
    }

    // ------------------------------------------------------------ rendering frame host

    public async Task ShowAsync(NSViewController controller, object? parameter)
    {
        await _paneGate.WaitAsync();
        try
        {
            if (_released) { await Dispatcher.ExecuteOnUIThread(controller.Dispose); return; }
            if (controller is CalendarEventComposePageViewController composer)
            {
                await ReplaceComposerAsync(composer);
                if (!_released) await composer.ActivateAsync(NavigationMode.New, parameter);
                return;
            }
            await ReplaceDetailsAsync(controller);
            if (controller is IWinoViewController next) await next.ActivateAsync(NavigationMode.New, parameter);
        }
        finally { _paneGate.Release(); }
    }

    public async Task ClearAsync()
    {
        await _paneGate.WaitAsync();
        try { await ReplaceDetailsAsync(null); }
        finally { _paneGate.Release(); }
    }

    private async Task ReplaceDetailsAsync(NSViewController? next)
    {
        var previous = _detailsChild;
        if (previous is IWinoViewController old)
        {
            try { await old.ReleaseAsync(); }
            catch (Exception exception) { ReportError(exception); }
        }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (previous is not null)
            {
                previous.View.RemoveFromSuperview();
                previous.RemoveFromParentViewController();
                previous.Dispose();
            }
            _detailsChild = next;
            bool show = next is not null && !_released;
            _detailsZone.Hidden = !show;
            _detailsLeading.Active = show;
            if (next is null) return;
            AddChildViewController(next);
            WinoLayout.Fill(next.View, _detailsContent);
        });
    }

    private async Task ReplaceComposerAsync(CalendarEventComposePageViewController? next)
    {
        var previous = _composer;
        var sheet = _composerSheet;
        _composer = null; _composerSheet = null;
        if (previous is not null)
        {
            try { await previous.ReleaseAsync(); }
            catch (Exception exception) { ReportError(exception); }
        }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (sheet is not null)
            {
                View.Window?.EndSheet(sheet);
                sheet.OrderOut(null);
            }
            previous?.Dispose();
            if (next is null || View.Window is not { } window) { next?.Dispose(); return; }
            _composer = next;
            _composerSheet = next.CreateSheetWindow();
            next.CloseRequested += ComposerCloseRequested;
            window.BeginSheet(_composerSheet, _ => { });
        });
    }

    private void ComposerCloseRequested(object? sender, EventArgs args) => Observe(CloseComposerAsync());

    private async Task CloseComposerAsync()
    {
        await _paneGate.WaitAsync();
        try { await ReplaceComposerAsync(null); }
        finally { _paneGate.Release(); }
    }

#if DEBUG
    private string DebugDrag(string[] args, bool resize)
    {
        if (args.Length < 2) return resize ? "usage: cal-resize N MINUTES [commit]" : "usage: cal-move N MINUTES [DAYS] [commit]";
        var visible = VisibleItems();
        int index = int.Parse(args[0]);
        if (index < 0 || index >= visible.Count) return $"only {visible.Count} visible items";
        var item = visible[index];
        double minutes = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        int days = !resize && args.Length > 2 && int.TryParse(args[2], out var parsed) ? parsed : 0;
        bool commit = args.Any(arg => arg.Equals("commit", StringComparison.OrdinalIgnoreCase));
        string before = $"{item.StartDate:g}–{item.EndDate:t}";
        if (!_surface.PreviewDrag(item, days, minutes, resize)) return "no tile on screen for " + item.Title;
        if (!_surface.IsDraggingItem) return $"refused '{item.Title}' ({CalendarTileMapper.DragRefusal(item)})";
        var target = _surface.CurrentDragTarget;
        if (commit) _surface.FinishPreviewDrag(true);
        return $"'{item.Title}' {before} -> {target?.Start:g}–{target?.End:t}{(commit ? " committed" : " (preview)")}";
    }

    private List<CalendarItemViewModel> VisibleItems()
        => ViewModel.CalendarItems.Where(item => ViewModel.CurrentVisibleRange?.Contains(item.StartDate) == true).OrderBy(item => item.StartDate).ToList();

    /// <summary>
    /// Debug bridge commands: calprev, calnext, caltoday, caltype Day|Week|WorkWeek|Month, calselect [N], calnew, calslot,
    /// cal-readonly-info (read-only calendars, visible read-only items, quick event and details state), cal-details N.
    /// </summary>
    private void RegisterDebugCommands()
    {
        MacDebugBridge.Register("calprev", _ => { _shell.PreviousDateRangeCommand.Execute(null); return Task.FromResult("ok"); });
        MacDebugBridge.Register("calnext", _ => { _shell.NextDateRangeCommand.Execute(null); return Task.FromResult("ok"); });
        MacDebugBridge.Register("caltoday", _ => { _shell.TodayClickedCommand.Execute(null); return Task.FromResult("ok"); });
        MacDebugBridge.Register("caltype", args => { ViewModel.StatePersistanceService.CalendarDisplayType = Enum.Parse<CalendarDisplayType>(args[0], true); return Task.FromResult("ok"); });
        MacDebugBridge.Register("calselect", args =>
        {
            int index = args.Length > 0 ? int.Parse(args[0]) : 0;
            var visible = ViewModel.CalendarItems.Where(item => ViewModel.CurrentVisibleRange?.Contains(item.StartDate) == true).OrderBy(item => item.StartDate).ToList();
            if (index < 0 || index >= visible.Count) return Task.FromResult($"only {visible.Count} visible items");
            WeakReferenceMessenger.Default.Send(new CalendarItemTappedMessage(visible[index]));
            return Task.FromResult("selected " + visible[index].Title);
        });
        MacDebugBridge.Register("calinfo", _ => Task.FromResult(
            $"range={ViewModel.CurrentVisibleRange?.StartDate}..{ViewModel.CurrentVisibleRange?.EndDate} items={ViewModel.CalendarItems.Count} " +
            $"active={_shell.AccountCalendarStateService.ActiveCalendars.Count()} groups={_shell.AccountCalendarStateService.GroupedAccountCalendars.Count} " +
            $"all={_shell.AccountCalendarStateService.AllCalendars.Count()} ready={ViewModel.Readiness.IsReady} surfaceItems={_surface.Items.Count} details={_detailsChild?.GetType().Name}"));
        MacDebugBridge.Register("calnew", args =>
        {
            // The shell's NewEvent needs the calendar picker dialog; open the composer directly for a visual check.
            // "calnew ACCOUNT" picks the first active calendar of the account whose address contains ACCOUNT.
            var start = (ViewModel.CurrentVisibleRange?.Dates.FirstOrDefault() ?? _dateContext.GetToday()).ToDateTime(new TimeOnly(10, 0));
            var calendar = _shell.AccountCalendarStateService.ActiveCalendars.FirstOrDefault(item => args.Length == 0
                || (item.MailAccount?.Address?.Contains(args[0], StringComparison.OrdinalIgnoreCase) ?? false));
            _navigation.Navigate(WinoPage.CalendarEventComposePage, new CalendarEventComposeNavigationArgs
            {
                SelectedCalendarId = calendar?.Id, StartDate = start, EndDate = start.AddMinutes(30), Title = "Design sync · macOS shell", Location = "Microsoft Teams"
            });
            return Task.FromResult("ok");
        });
        MacDebugBridge.Register("cal-readonly-info", _ =>
        {
            var calendars = _shell.AccountCalendarStateService.AllCalendars.Where(calendar => calendar.IsReadOnly).Select(calendar => $"{calendar.Name} ({calendar.Id})").ToList();
            var visible = VisibleItems();
            var readOnlyItems = visible.Select((item, index) => (item, index)).Where(pair => CalendarTileMapper.IsReadOnly(pair.item)).Select(pair => $"{pair.index}:{pair.item.Title}").ToList();
            var quick = ViewModel.SelectedQuickEventAccountCalendar;
            return Task.FromResult($"readOnlyCalendars=[{string.Join("; ", calendars)}] visibleReadOnlyItems=[{string.Join("; ", readOnlyItems)}] " +
                $"quickCalendar={quick?.Name} quickReadOnly={quick?.IsReadOnly} canSaveQuick={ViewModel.CanSaveQuickEvent} details={(_detailsChild as EventDetailsPageViewController)?.DescribeReadOnlyState()}");
        });
        MacDebugBridge.Register("cal-details", async args =>
        {
            // "cal-details N" opens the details of the Nth visible item and reports the read-only presentation.
            int index = args.Length > 0 ? int.Parse(args[0]) : 0;
            var visible = VisibleItems();
            if (index < 0 || index >= visible.Count) return $"only {visible.Count} visible items";
            WeakReferenceMessenger.Default.Send(new CalendarItemTappedMessage(visible[index]));
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100);
                if (_detailsChild is EventDetailsPageViewController details && ReferenceEquals(ViewModel.DisplayDetailsCalendarItemViewModel, visible[index]))
                    return details.DescribeReadOnlyState();
            }
            return "details did not open";
        });
        // "cal-move N MINUTES [DAYS] [commit]" drags the Nth visible item (ghost only, unless commit);
        // "cal-resize N MINUTES [commit]" drags its bottom edge; "cal-drag-off" removes a preview ghost.
        MacDebugBridge.Register("cal-move", args => Task.FromResult(DebugDrag(args, resize: false)));
        MacDebugBridge.Register("cal-resize", args => Task.FromResult(DebugDrag(args, resize: true)));
        MacDebugBridge.Register("cal-drag-off", _ => { _surface.FinishPreviewDrag(false); return Task.FromResult("ok"); });
        // "cal-details-showas N VALUE", "cal-details-reminder N MINUTES", "cal-attach-save N" act on the open details.
        MacDebugBridge.Register("cal-details-showas", args => _detailsChild is EventDetailsPageViewController details && args.Length > 0
            ? details.DebugChooseShowAsAsync(args[^1]) : Task.FromResult("open an event's details first (cal-details N)"));
        MacDebugBridge.Register("cal-details-reminder", args => _detailsChild is EventDetailsPageViewController details && args.Length > 0 && int.TryParse(args[^1], out var minutes)
            ? details.DebugToggleReminderAsync(minutes) : Task.FromResult("open an event's details first (cal-details N)"));
        MacDebugBridge.Register("cal-attach-save", args => Task.FromResult(_detailsChild is EventDetailsPageViewController details
            ? details.DebugSaveAttachment(args.Length > 0 ? int.Parse(args[0]) : 0) : "open an event's details first (cal-details N)"));
        // "calnew-notes HTML" opens the composer with imported notes (check with cal-notes).
        MacDebugBridge.Register("calnew-notes", args =>
        {
            var start = (ViewModel.CurrentVisibleRange?.Dates.FirstOrDefault() ?? _dateContext.GetToday()).ToDateTime(new TimeOnly(10, 0));
            var calendar = _shell.AccountCalendarStateService.ActiveCalendars.FirstOrDefault(item => !item.IsReadOnly);
            _navigation.Navigate(WinoPage.CalendarEventComposePage, new CalendarEventComposeNavigationArgs
            {
                SelectedCalendarId = calendar?.Id, StartDate = start, EndDate = start.AddMinutes(30), Title = "Notes check",
                NotesHtml = args.Length > 0 ? string.Join(' ', args) : "<div dir=\"ltr\"><b>Agenda</b>&nbsp;<a href=\"https://example.com\">link</a></div>\r\n"
            });
            return Task.FromResult("ok");
        });
        MacDebugBridge.Register("calslot", _ =>
        {
            var date = ViewModel.CurrentVisibleRange?.Dates.FirstOrDefault() ?? _dateContext.GetToday();
            SurfaceSlotClicked(this, new CalendarSlotClickedEventArgs(date.ToDateTime(new TimeOnly(10, 0)), new CGRect(WinoCalendarSurfaceView.HourColumnWidth, 200, 120, 26), false));
            return Task.FromResult("ok");
        });
    }
#endif

    // ------------------------------------------------------------ lifecycle

    protected override async Task DeactivateAsync()
    {
        _released = true;
        _navigation.DetachRenderingHost(this);
        CloseQuickEvent();
        await _paneGate.WaitAsync();
        try
        {
            await ReplaceDetailsAsync(null);
            await ReplaceComposerAsync(null);
        }
        finally { _paneGate.Release(); }
        if (_selected is not null) { _selected.IsSelected = false; _selected = null; }
        ViewModel.OnNavigatedFrom(NavigationMode.New, null!);
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released = true;
            _navigation.DetachRenderingHost(this);
            CloseQuickEvent();
            var child = _detailsChild; _detailsChild = null;
            if (child is not null) { child.View.RemoveFromSuperview(); child.RemoveFromParentViewController(); child.Dispose(); }
            if (_composerSheet is not null) { View.Window?.EndSheet(_composerSheet); _composerSheet.OrderOut(null); _composerSheet = null; }
            _composer?.Dispose(); _composer = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>Blocked state shown instead of the grid (Windows ModeReadinessPanel): title, message, progress.</summary>
internal sealed class CalendarReadinessView : NSView
{
    private readonly NSTextField _title;
    private readonly NSTextField _message;
    private readonly NSProgressIndicator _progress;

    public CalendarReadinessView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var icon = new WinoIconView(WinoIconGlyph.Calendar, 40, WinoStyle.TertiaryText);
        _title = WinoStyle.Label(string.Empty, WinoStyle.Heading, WinoStyle.PrimaryText, 0);
        _title.Alignment = NSTextAlignment.Center;
        _message = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        _message.Alignment = NSTextAlignment.Center;
        _progress = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        var stack = WinoLayout.VStack(10, icon, _title, _message, _progress);
        stack.Alignment = NSLayoutAttribute.CenterX;
        AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            stack.WidthAnchor.ConstraintLessThanOrEqualTo(420)
        ]);
    }

    public void Update(string title, string message, bool progress)
    {
        _title.StringValue = title ?? string.Empty;
        _message.StringValue = message ?? string.Empty;
        _progress.Hidden = !progress;
        if (progress) _progress.StartAnimation(null); else _progress.StopAnimation(null);
    }
}

/// <summary>Labels and glyphs for the shared calendar context menu actions (Windows CalendarItemCommandBarFlyout).</summary>
internal static class CalendarContextMenuLabels
{
    public static string Label(CalendarContextMenuAction action)
    {
        if (action.ActionType == CalendarContextMenuActionType.ShowAs && action.ShowAs is { } showAs && action.TargetType is null)
            return ShowAsText(showAs);
        if (action.ActionType == CalendarContextMenuActionType.Respond && action.ResponseStatus is { } status && action.TargetType is null)
            return status switch
            {
                CalendarItemStatus.Accepted => Wino.Core.Domain.Translator.CalendarEventResponse_Accept,
                CalendarItemStatus.Tentative => Wino.Core.Domain.Translator.CalendarEventResponse_Tentative,
                CalendarItemStatus.Cancelled => Wino.Core.Domain.Translator.CalendarEventResponse_Decline,
                _ => Wino.Core.Domain.Translator.CalendarEventResponse_Accept
            };
        if (action.TargetType.HasValue && action.ActionType is CalendarContextMenuActionType.Delete or CalendarContextMenuActionType.ShowAs or CalendarContextMenuActionType.Respond)
            return action.TargetType == CalendarEventTargetType.Single
                ? Wino.Core.Domain.Translator.CalendarContextMenu_ThisEventOnly
                : Wino.Core.Domain.Translator.CalendarContextMenu_AllEventsInSeries;
        return action.ActionType switch
        {
            CalendarContextMenuActionType.Open when action.TargetType == CalendarEventTargetType.Series => Wino.Core.Domain.Translator.CalendarItem_DetailsPopup_ViewSeriesButton,
            CalendarContextMenuActionType.Open => Wino.Core.Domain.Translator.Buttons_Open,
            CalendarContextMenuActionType.JoinOnline => Wino.Core.Domain.Translator.CalendarItem_DetailsPopup_JoinOnline,
            CalendarContextMenuActionType.Delete => Wino.Core.Domain.Translator.Buttons_Delete,
            CalendarContextMenuActionType.ShowAs => Wino.Core.Domain.Translator.CalendarEventDetails_ShowAs,
            CalendarContextMenuActionType.Respond => Wino.Core.Domain.Translator.CalendarContextMenu_Respond,
            _ => Wino.Core.Domain.Translator.Buttons_Open
        };
    }

    /// <summary>
    /// Windows CalendarItemCommandBarFlyout: busy items take no action, and a read-only calendar's
    /// events cannot be deleted, re-marked (Show as) or answered.
    /// </summary>
    public static bool IsEnabled(CalendarContextMenuItem entry, CalendarItemViewModel item)
    {
        bool isMutation = entry.Action.ActionType is CalendarContextMenuActionType.Delete
            or CalendarContextMenuActionType.ShowAs or CalendarContextMenuActionType.Respond;
        return entry.IsEnabled && !item.IsBusy && (!isMutation || !CalendarTileMapper.IsReadOnly(item));
    }

    public static string ShowAsText(CalendarItemShowAs showAs) => showAs switch
    {
        CalendarItemShowAs.Free => Wino.Core.Domain.Translator.CalendarShowAs_Free,
        CalendarItemShowAs.Tentative => Wino.Core.Domain.Translator.CalendarShowAs_Tentative,
        CalendarItemShowAs.OutOfOffice => Wino.Core.Domain.Translator.CalendarShowAs_OutOfOffice,
        CalendarItemShowAs.WorkingElsewhere => Wino.Core.Domain.Translator.CalendarShowAs_WorkingElsewhere,
        _ => Wino.Core.Domain.Translator.CalendarShowAs_Busy
    };

    public static WinoIconGlyph Glyph(CalendarContextMenuAction action) => action.ActionType switch
    {
        CalendarContextMenuActionType.Open => action.TargetType == CalendarEventTargetType.Series ? WinoIconGlyph.EventEditSeries : WinoIconGlyph.Calendar,
        CalendarContextMenuActionType.JoinOnline => WinoIconGlyph.EventJoinOnline,
        CalendarContextMenuActionType.Delete => action.TargetType is null ? WinoIconGlyph.Delete : WinoIconGlyph.None,
        CalendarContextMenuActionType.ShowAs => action.TargetType is null && action.ShowAs is null ? WinoIconGlyph.CalendarShowAs : WinoIconGlyph.None,
        CalendarContextMenuActionType.Respond => action.ResponseStatus switch
        {
            CalendarItemStatus.Accepted when action.TargetType is null => WinoIconGlyph.EventAccept,
            CalendarItemStatus.Tentative when action.TargetType is null => WinoIconGlyph.EventTentative,
            CalendarItemStatus.Cancelled when action.TargetType is null => WinoIconGlyph.EventDecline,
            null => WinoIconGlyph.EventRespond,
            _ => WinoIconGlyph.None
        },
        _ => WinoIconGlyph.None
    };
}
