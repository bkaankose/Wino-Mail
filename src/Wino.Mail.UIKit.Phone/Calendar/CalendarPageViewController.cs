using CoreGraphics;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Calendar;
using Wino.Mail.UIKit.Phone.Mail;
using Wino.Mail.UIKit.Views.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The iPhone Calendar tab (iOS design decisions): the range as the title with Today, New event and the calendars
/// filter in the bar; a header zone with the collapsible month picker and the period row (Day · Week · Month, previous,
/// next, agenda toggle); and the content zone with the Windows timed surface (one column for Day) or the agenda list.
/// A tile tap pushes the event details, the composer opens as a full-screen sheet, and a long press on empty time
/// opens the quick event sheet; a long press on a tile moves it (or resizes it from its bottom edge).
/// Partial files: Agenda (the list), Debug (bridge commands).
/// </summary>
public sealed partial class CalendarPageViewController : CalendarPageViewControllerBase
{
    private readonly PhoneShellHost _host;
    private CalendarPage? _page;
    private UINavigationController? _calendarsSheet;
    private bool _agendaOn;
    private bool _headerExpanded;

    public CalendarPageViewController(CalendarPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
        CalendarAppShellViewModel shell, IContextMenuItemService contextMenus, IDateContextProvider dateContext, IMailDialogService dialogs,
        IServiceProvider services, PhoneShellHost host)
        : base(viewModel, dispatcher, logger, shell, contextMenus, dateContext, dialogs, services)
    {
        _host = host;
        Title = Translator.KeyboardShortcuts_ModeCalendar;
    }

    public override void LoadView()
    {
        var page = new CalendarPage();
        _page = page;
        PlaceSurface(page.ContentZone.ContentView);
        BuildAgenda(page.ContentZone.ContentView);

        page.MiniCalendar.Collapsed = true;
        page.MiniCalendar.SetToggle(WinoIconGlyph.ChevronDown, Translator.KeyboardShortcuts_ModeCalendar);
        page.MiniCalendar.DateClicked += (_, date) => GoToDate(date);
        page.MiniCalendar.ToggleClicked += (_, _) => SetHeaderExpanded(!_headerExpanded);
        page.Period.ValueChanged += (_, _) =>
        {
            var index = (int)page.Period.SelectedSegment;
            if (index >= 0 && index < CalendarPage.DisplayTypes.Length) RequestDisplayType(CalendarPage.DisplayTypes[index]);
        };
        page.Previous.PrimaryActionTriggered += (_, _) => GoPrevious();
        page.Next.PrimaryActionTriggered += (_, _) => GoNext();
        page.Agenda.PrimaryActionTriggered += (_, _) => SetAgenda(!_agendaOn);
        page.SetAgendaOn(false);

        // A horizontal swipe on the surface pages the period, like the iOS Calendar day view.
        Surface.AddGestureRecognizer(new UISwipeGestureRecognizer(GoNext) { Direction = UISwipeGestureRecognizerDirection.Left });
        Surface.AddGestureRecognizer(new UISwipeGestureRecognizer(GoPrevious) { Direction = UISwipeGestureRecognizerDirection.Right });

        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        var today = new UIBarButtonItem(Translator.Today, UIBarButtonItemStyle.Plain, (_, _) => GoToday());
        NavigationItem.LeftBarButtonItem = today;
        View = page;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        // The shell placed its avatar before activation; New event and the calendars filter join it.
        var items = new List<UIBarButtonItem>();
        if (NavigationItem.RightBarButtonItem is { } avatar) items.Add(avatar);
        items.Add(PhoneBarItems.Button(WinoIconGlyph.Add, Translator.CalendarEventCompose_NewEventButton, NewEvent));
        items.Add(PhoneBarItems.Button(WinoIconGlyph.Filter, Translator.IOS_Calendar_Calendars, ShowCalendars));
        NavigationItem.RightBarButtonItems = items.ToArray();
        return base.InitializeAsync(mode, parameter);
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        // Back from the event details: the next tap on the same tile must route again.
        if (!IsMovingToParentViewController && IsActive && CurrentDetails is null) ResetDetailsItem();
    }

    protected override EventDetailsPageViewController? CurrentDetails
        => NavigationController?.ViewControllers?.OfType<EventDetailsPageViewController>().LastOrDefault();

    /// <summary>A tap on empty time does nothing on the phone; a long press (a range, even one slot) opens the quick event sheet.</summary>
    protected override (UIView? View, CGRect Rect)? QuickEventAnchor(CalendarSlotClickedEventArgs args)
        => args.End is null ? null : (null, CGRect.Empty);

    protected override Task DetailsClearedAsync()
    {
        if (CurrentDetails is not null && NavigationController is { } stack) stack.PopToViewController(this, true);
        return Task.CompletedTask;
    }

    protected override void VisibleRangeChanged(VisibleDateRange? range)
    {
        if (_page is not { } page) return;
        if (range is null) { page.MiniCalendar.SelectedDates = new HashSet<DateOnly>(); return; }
        page.MiniCalendar.SelectedDates = new HashSet<DateOnly>(range.Dates);
        page.MiniCalendar.DisplayMonth = range.AnchorDate;
        ReloadAgenda();
    }

    protected override void SettingsChanged(CalendarSettings? settings)
    {
        if (_page is { } page && settings is not null) page.MiniCalendar.FirstDayOfWeek = settings.FirstDayOfWeek;
    }

    protected override void ItemsChanged()
    {
        ObserveAgendaItems();
        ReloadAgenda();
    }

    protected override void DisplayTypeChanged(CalendarDisplayType type)
    {
        if (_page is { } page) page.Period.SelectedSegment = CalendarPage.SegmentFor(type);
    }

    protected override void RangeTextChanged(string? text) => NavigationItem.Title = string.IsNullOrWhiteSpace(text) ? Translator.KeyboardShortcuts_ModeCalendar : text;

    protected override void ReadinessChanged(bool blocked) => ApplyContentVisibility();

    private void SetHeaderExpanded(bool expanded)
    {
        if (_page is not { } page) return;
        _headerExpanded = expanded;
        page.MiniCalendar.Collapsed = !expanded;
        page.MiniCalendar.SetToggle(expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, Translator.KeyboardShortcuts_ModeCalendar);
        UIView.Animate(0.2, () => page.LayoutIfNeeded());
    }

    private void SetAgenda(bool on)
    {
        _agendaOn = on;
        _page?.SetAgendaOn(on);
        ApplyContentVisibility();
        if (on) ReloadAgenda();
    }

    private void ApplyContentVisibility()
    {
        bool blocked = ViewModel.Readiness.IsBlocked;
        Surface.Hidden = blocked || _agendaOn;
        SetAgendaHidden(blocked || !_agendaOn);
    }

    /// <summary>The calendars filter: accounts and calendars with colour toggles, as a sheet.</summary>
    private void ShowCalendars()
    {
        if (_calendarsSheet is { PresentingViewController: not null }) return;
        var content = new CalendarsSheetViewController(Shell.AccountCalendarStateService, () =>
        {
            if (_calendarsSheet is { } sheet) _ = _host.DismissSheetAsync(sheet);
            _calendarsSheet = null;
        });
        _calendarsSheet = _host.PresentSheet(content, PhoneSheetSize.MediumAndLarge, () => _calendarsSheet = null);
    }

    /// <summary>The event composer this page took from the rendering frame: a full-screen sheet.</summary>
    protected override bool ShowComposer(CalendarEventComposePageViewController composer)
        => CalendarComposerSheet.Present(_host, Services, composer);

    /// <summary>A composer sheet still open when the page leaves closes with it (its draft state is the composer's).</summary>
    protected override Task ReleaseHostedPagesAsync()
        => CalendarComposerSheet.Current is { } sheet ? sheet.CloseAsync() : Task.CompletedTask;

    protected override void ReleaseIdiom()
    {
        ReleaseAgenda();
        if (_calendarsSheet is { } sheet)
        {
            _calendarsSheet = null;
            _ = _host.DismissSheetAsync(sheet);
        }
    }
}
