using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Calendar;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Mail.ViewModels.Calendar.Messages;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Event interactions (macOS CalendarPageViewController surface handlers): a click selects the event and fills the
/// details zone, a double click (or Return) opens it, the context menu runs the shared calendar menu entries, and the
/// selected event carries the selection ring. An empty slot's menu offers New event and Paste; Ctrl+C and Ctrl+V copy
/// and paste the selected event, and a double click on a month day opens it in the Day view.
/// </summary>
internal sealed partial class CalendarPageView
{
    private CalendarItemViewModel? _selected;
    private ICalendarItem? _revealPending;
    private WinoMenuPopover? _openMenu;
    private WinoCalendarTile? _openMenuTile;
    private CalendarItemTarget? _pendingTarget;

    partial void CloseQuickEvent();

    partial void BuildInteraction()
    {
        if (_surface is not { } surface) return;
        EventHandler<CalendarItemEventArgs> clicked = (_, args) => SurfaceItemClicked(args);
        EventHandler<CalendarItemEventArgs> doubleClicked = (_, args) => SurfaceItemDoubleClicked(args);
        EventHandler<CalendarItemEventArgs> context = (_, args) => SurfaceItemContextRequested(args);
        EventHandler<CalendarSlotEventArgs> slotClicked = (_, args) => SurfaceSlotClicked(args);
        EventHandler<CalendarSlotEventArgs> slotContext = (_, args) => SurfaceSlotContextRequested(args);
        EventHandler relaid = (_, _) => SurfaceRelaid(surface);
        EventHandler<DateOnly> monthDayDoubleClicked = (_, day) => SurfaceMonthDayDoubleClicked(day);
        EventHandler copy = (_, _) => CopySelected();
        EventHandler<CalendarSlotEventArgs> paste = (_, args) => Paste(args);
        EventHandler quickEventCancelled = (_, _) => CloseQuickEvent();
        _activated.Add(() =>
        {
            surface.ItemClicked += clicked;
            surface.ItemDoubleClicked += doubleClicked;
            surface.ItemContextRequested += context;
            surface.SlotClicked += slotClicked;
            surface.SlotContextRequested += slotContext;
            surface.Relaid += relaid;
            surface.MonthDayDoubleClicked += monthDayDoubleClicked;
            surface.CopyRequested += copy;
            surface.PasteRequested += paste;
            ViewModel.QuickEventCancellationRequested += quickEventCancelled;
            Bind.Bind(nameof(CalendarPageViewModel.DisplayDetailsCalendarItemViewModel), static vm => vm.DisplayDetailsCalendarItemViewModel, DetailsItemChanged);
            Subscribe<CalendarItemUpdated>(_ => surface.RefreshTiles());
#if DEBUG
            RegisterInteractionDebugVerbs(surface);
#endif
        });
        _deactivating.Add(() =>
        {
            surface.ItemClicked -= clicked;
            surface.ItemDoubleClicked -= doubleClicked;
            surface.ItemContextRequested -= context;
            surface.SlotClicked -= slotClicked;
            surface.SlotContextRequested -= slotContext;
            surface.Relaid -= relaid;
            surface.MonthDayDoubleClicked -= monthDayDoubleClicked;
            surface.CopyRequested -= copy;
            surface.PasteRequested -= paste;
            ViewModel.QuickEventCancellationRequested -= quickEventCancelled;
            CloseMenu();
            if (_selected is not null) _selected.IsSelected = false;
            _selected = null;
            _revealPending = null;
            return Task.CompletedTask;
        });
    }

    private void SurfaceItemClicked(CalendarItemEventArgs args)
    {
        CloseQuickEvent();
        if (args.Item is CalendarItemViewModel item) Messenger.Send(new CalendarItemTappedMessage(item));
    }

    private void SurfaceItemDoubleClicked(CalendarItemEventArgs args)
    {
        CloseQuickEvent();
        if (args.Item is CalendarItemViewModel item) Messenger.Send(new CalendarItemDoubleTappedMessage(item));
    }

    private void SurfaceItemContextRequested(CalendarItemEventArgs args)
    {
        CloseQuickEvent();
        if (args.Item is not CalendarItemViewModel item) return;
        Messenger.Send(new CalendarItemRightTappedMessage(item));
        var builder = WinoMenus.Create();
        CalendarContextMenus.Append(builder, MenuEntries(item));
        ShowMenu(args.Tile, builder, args.X, args.Y);
    }

    /// <summary>A click on an empty slot or a dragged range: with an event selected it only clears the selection (macOS), else it opens the quick event.</summary>
    private void SurfaceSlotClicked(CalendarSlotEventArgs args)
    {
        if (ViewModel.DisplayDetailsCalendarItemViewModel is not null)
        {
            ViewModel.DisplayDetailsCalendarItemViewModel = null;
            return;
        }
        OpenQuickEvent(args);
    }

    partial void OpenQuickEvent(CalendarSlotEventArgs args);

    private void SurfaceSlotContextRequested(CalendarSlotEventArgs args)
    {
        CloseQuickEvent();
        if (!ViewModel.Readiness.IsReady) return;
        var builder = WinoMenus.Create();
        var start = args.Start;
        var isAllDay = args.IsAllDay;
        builder.Item(Translator.CalendarEventCompose_NewEventButton, () => MainLoop.Post(() => StartNewEvent(start, isAllDay)));
        builder.Item(Translator.CalendarContextMenu_Paste, () => Paste(args), CanPaste);
        CloseMenu();
        _openMenu = ContextMenu.Show(args.AnchorWidget, builder.Build(), args.Anchor.X, args.Anchor.Y, () => _openMenu = null);
    }

    /// <summary>A double click on a month day opens that day in the Day view (Windows, macOS).</summary>
    private void SurfaceMonthDayDoubleClicked(DateOnly day)
    {
        CloseQuickEvent();
        _shell.OpenDayView(day);
    }

    private bool CanPaste => ViewModel.HasCopiedCalendarItem && ViewModel.Readiness.IsReady;

    /// <summary>Ctrl+C on the surface copies the selected event, as its Copy menu item does.</summary>
    private void CopySelected()
    {
        if (_selected is { } item) Observe(ViewModel.CopyCalendarItemAsync(item));
    }

    /// <summary>Pastes the copied event at a slot: a timed slot moves a timed event's start there, a day keeps its time of day.</summary>
    private void Paste(CalendarSlotEventArgs args)
    {
        if (!CanPaste || IsReleased) return;
        Observe(ViewModel.PasteCalendarItemAsync(args.Start, !args.IsAllDay));
    }

    /// <summary>New event from a slot: the quick event when it exists, otherwise the composer for that slot.</summary>
    private void StartNewEvent(DateTime start, bool isAllDay)
    {
        if (IsReleased) return;
        ViewModel.BeginQuickEvent(start, null, isAllDay);
        if (ViewModel.GoToEventComposePageCommand.CanExecute(null)) ViewModel.GoToEventComposePageCommand.Execute(null);
    }

    private CalendarMenuEntry[] MenuEntries(CalendarItemViewModel item)
        => CalendarContextMenus.Build(_contextMenus.GetCalendarItemContextMenuItems(item.CalendarItem), item,
            action => MainLoop.Post(() =>
            {
                if (!IsReleased) Messenger.Send(new CalendarItemContextActionRequestedMessage(item, action));
            }));

    private void ShowMenu(WinoCalendarTile tile, WinoMenuBuilder builder, double x, double y)
    {
        CloseMenu();
        _openMenuTile = tile;
        _openMenu = ContextMenu.Show(tile.Root, builder.Build(), x, y, () =>
        {
            _openMenu = null;
            _openMenuTile = null;
        });
    }

    private void CloseMenu()
    {
        _openMenu?.Popdown();
        _openMenu = null;
        _openMenuTile = null;
    }

    /// <summary>The selection ring follows the details item; a selected event is brought into view once its tile exists.</summary>
    private void DetailsItemChanged(CalendarItemViewModel? item)
    {
        if (!ReferenceEquals(_selected, item))
        {
            if (_selected is not null) _selected.IsSelected = false;
            _selected = item;
            if (item is not null) item.IsSelected = true;
            _surface?.RefreshTiles();
        }
        _revealPending = item;
        if (item is not null && _surface?.RevealItem(item) == true) _revealPending = null;
        if (item is null) Observe(ClearAsync());
        else if (ViewModel.NavigateEventDetailsCommand.CanExecute(null)) ViewModel.NavigateEventDetailsCommand.Execute(null);
    }

    /// <summary>
    /// The event a page was opened for (an invitation's Open in calendar, a notification). The shared pending target can
    /// miss a recurring meeting (it looks for the series, the page loads occurrences), so once the event or its occurrence
    /// on that day is among the loaded items it is selected the way a click selects it.
    /// </summary>
    private void OpenPendingTarget()
    {
        if (IsReleased || _pendingTarget is not { } target) return;
        if (ViewModel.DisplayDetailsCalendarItemViewModel?.Id == target.Item.Id)
        {
            _pendingTarget = null;
            return;
        }
        // A series target (an invitation of a recurring meeting) matches its occurrence on the target's day.
        var day = DateOnly.FromDateTime(target.Item.LocalStartDate);
        CalendarItemViewModel? match = null;
        foreach (var item in ViewModel.CalendarItems)
        {
            if (item.Id == target.Item.Id)
            {
                match = item;
                break;
            }
            if (match is null && item.CalendarItem.RecurringCalendarItemId == target.Item.Id && DateOnly.FromDateTime(item.StartDate) == day) match = item;
        }
        if (match is null) return;
        _pendingTarget = null;
        Messenger.Send(new CalendarItemTappedMessage(match));
    }

    /// <summary>A relayout recycles tiles: a menu whose tile lost its event closes, and a pending selection is revealed.</summary>
    private void SurfaceRelaid(WinoCalendarSurface surface)
    {
        if (_openMenuTile is { Item: null }) CloseMenu();
        if (_pendingTarget is not null) MainLoop.Post(OpenPendingTarget);
        if (_revealPending is { } pending && surface.RevealItem(pending)) _revealPending = null;
    }
}
