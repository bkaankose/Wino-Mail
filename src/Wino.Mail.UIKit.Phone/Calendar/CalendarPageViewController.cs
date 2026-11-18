using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Calendar;
using Wino.Mail.UIKit.Phone.Mail;
using Wino.Mail.UIKit.Phone.Shell;
using Wino.Mail.UIKit.Views.Calendar;
using Wino.Presentation.UIKit;
using Wino.Mail.ViewModels.Calendar;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The Calendar tab on iPhone and iPad (calendar refresh of 14 Nov 2026, docs/design-prototypes/ios-refresh-20261114):
/// the shell's account avatar leading with the date title beside it ("Sat, Nov 14", "Nov 9 – 15", "November 2026"),
/// which opens the attached date selector (<see cref="CalendarDatePanelView"/>); trailing, the view menu (Day, Week,
/// Work week on iPad, Month, Show as list, Calendars) and the event search. New event is the shell's floating button; a
/// Today capsule floats at the bottom leading edge while today is off screen. Day adds a week strip under the bar
/// (<see cref="CalendarWeekStripView"/>); a narrow Month shows dots and the selected day's events under the grid. The
/// surface's rail follows the finger through the periods; a tap on a Week header day or a double tap on a month cell
/// opens that day. A tile tap pushes the event details, the composer opens as a full-screen sheet, and a long press on
/// empty time opens the quick event sheet; a long press on a tile moves it (or resizes it from its bottom edge).
/// Partial files: Agenda (the list and the month day list), Search (the event search), Debug (bridge commands).
/// </summary>
public sealed partial class CalendarPageViewController : CalendarPageViewControllerBase, ICalendarPickerHost, IPhoneFloatingActionPage, IPadItemOwner
{
    private readonly PhoneShellHost _host;
    private CalendarPage? _page;
    private bool _agendaOn;
    private CalendarDatePanelView? _panel;
    private UIButton? _titleButton;
    private UIBarButtonItem? _titleItem;
    private UIBarButtonItem? _viewItem;
    private CalendarTodayCapsule? _capsule;
    private CalendarPagingInteraction? _paging;
    private CalendarPagingInteraction? _stripPaging;
    private CalendarDisplayType _displayType = CalendarDisplayType.Week;
    private VisibleDateRange? _range;
    private VisibleDateRange? _viewportRange;
    private DateOnly? _monthSelection;
    private DateOnly? _pendingMonthSelection;
    private DateOnly? _stripSelection;
    private DayOfWeek _firstDayOfWeek = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

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
        PlaceSurface(page.SurfaceSlot);
        BuildMonthList(page.MonthListSlot);
        BuildAgenda(page.Body);
        Surface.AllDayMaxLanes = PhoneShellViewController.IsPad ? CalendarItemOverflow.MaxAllDayLanes : 2;
        Surface.AllDayLaneHeight = PhoneShellViewController.IsPad ? 24 : 22;
        Surface.DayOpenRequested += SurfaceDayOpenRequested;
        Surface.SlotClicked += SurfaceSlotSelected;
        Surface.HourHeightChanged += SurfaceHourHeightChanged;

        page.Strip.DateTapped += (_, date) => StripDateTapped(date);
        _stripPaging = new CalendarPagingInteraction(page.Strip, () => !ViewModel.Readiness.IsBlocked && _panel?.IsOpen != true,
            direction => StripDateTapped(page.Strip.SelectedDate.AddDays(7 * direction)));
        // The surface rails on its own; the agenda list still pages the period.
        _paging = new CalendarPagingInteraction(page.Body, () => _agendaOn && !ViewModel.Readiness.IsBlocked && _panel?.IsOpen != true,
            direction => { if (direction > 0) GoNext(); else GoPrevious(); });

        var capsule = new CalendarTodayCapsule(TodayCapsuleTapped);
        _capsule = capsule;
        page.AddSubview(capsule);

        var panel = new CalendarDatePanelView(this);
        _panel = panel;
        WinoLayout.Fill(panel, page);

        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        // The date is the leading title beside the avatar; an empty title view keeps the bar's centred title away.
        NavigationItem.TitleView = new UIView(CGRect.Empty);
        _titleItem = BuildTitleItem();
        _viewItem = new UIBarButtonItem(WinoIcons.TemplateImage(CalendarPage.GlyphOf(_displayType), 22), BuildViewMenu());
        _viewItem.AccessibilityLabel = Translator.IOS_Calendar_ViewMenu;
        NavigationItem.RightBarButtonItems = [_viewItem];
        BuildSearch();
        View = page;
        ApplyLayout(animated: false);
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        EnsureLeadingTitle();
        // Back from the event details: the next tap on the same tile must route again.
        if (!IsMovingToParentViewController && IsActive && CurrentDetails is null) ResetDetailsItem();
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        EnsureLeadingTitle();
    }

    /// <summary>
    /// The shell sets the account avatar as the leading item after the page loads, which replaces every leading item; the
    /// title goes back beside it.
    /// </summary>
    private void EnsureLeadingTitle()
    {
        if (_titleItem is not { } title) return;
        var items = NavigationItem.LeftBarButtonItems ?? [];
        if (items.Contains(title)) return;
        NavigationItem.LeftBarButtonItems = [.. items, title];
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        if (_page is not { } page || _capsule is not { } capsule) return;
        double floating = WinoStyle.ScaledIcon(ShellFloatingButton.Diameter, page.TraitCollection, 1.3);
        double tabTop = TabBarController?.TabBar is { Hidden: false, Window: not null } tabBar
            ? (double)page.ConvertRectFromView(tabBar.Bounds, tabBar).Top
            : (double)(page.Bounds.Height - page.SafeAreaInsets.Bottom);
        double centerY = tabTop - 16 - floating / 2;
        var size = capsule.IntrinsicContentSize;
        capsule.Frame = new CGRect(page.SafeAreaInsets.Left + 16, centerY - CalendarTodayCapsule.Height / 2, size.Width, CalendarTodayCapsule.Height);
        page.BringSubviewToFront(capsule);
        if (_panel is { } panel) page.BringSubviewToFront(panel);
        Surface.GridBottomInset = Math.Max(0, (double)page.Bounds.Height - tabTop) + 16;
        ApplyCompactMonth();
    }

    void IPadItemOwner.PadItemClosed() => ResetDetailsItem();

    protected override EventDetailsPageViewController? CurrentDetails
        => NavigationController?.LogicalViewControllers().OfType<EventDetailsPageViewController>().LastOrDefault();

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
        _range = range;
        _viewportRange = range;
        _panel?.Refresh();
        UpdateTitle();
        if (range is not null)
        {
            SettleMonthSelection(range);
            SyncStrip(range.DisplayType == CalendarDisplayType.Day ? range.AnchorDate : null, feedback: false);
            ReloadAgenda();
        }
        UpdateTodayCapsule();
    }

    protected override void SettingsChanged(CalendarSettings? settings)
    {
        if (settings is null) return;
        _firstDayOfWeek = settings.FirstDayOfWeek;
        _panel?.Refresh();
        SyncStrip(null, feedback: false);
    }

    protected override void ItemsChanged()
    {
        ObserveAgendaItems();
        ReloadAgenda();
    }

    protected override void DisplayTypeChanged(CalendarDisplayType type)
    {
        bool changed = type != _displayType;
        _displayType = type;
        if (changed && _page is { } page && page.Window is not null)
            UIView.Transition(page.Body, 0.2, UIViewAnimationOptions.TransitionCrossDissolve | UIViewAnimationOptions.AllowAnimatedContent, null, null);
        ApplyLayout(animated: changed);
        UpdateTitle();
        _panel?.Refresh();
        UpdateTodayCapsule();
    }

    /// <summary>The surface header, the week strip, the compact month and the view menu for the current view.</summary>
    private void ApplyLayout(bool animated)
    {
        // Day has no column header row: the strip names the days.
        Surface.ShowsDayHeader = _displayType != CalendarDisplayType.Day;
        bool strip = _displayType == CalendarDisplayType.Day || (_agendaOn && _displayType is CalendarDisplayType.Week or CalendarDisplayType.WorkWeek);
        _page?.SetStripVisible(strip && !ViewModel.Readiness.IsBlocked, animated);
        SyncStrip(null, feedback: false);
        ApplyCompactMonth();
        ApplyContentVisibility();
        UpdateViewMenu();
    }

    /// <summary>A narrow Month (every iPhone, iPad Split View) is the compact grid with the day list under it.</summary>
    private void ApplyCompactMonth()
    {
        if (_page is not { } page) return;
        double width = (double)page.Body.Bounds.Width;
        bool compact = _displayType == CalendarDisplayType.Month && !_agendaOn && !ViewModel.Readiness.IsBlocked
            && width > 0 && width / CalendarLayoutCalculator.MonthColumns < WinoCalendarSurfaceView.CompactMonthCellWidth;
        if (compact == page.IsCompactMonth) return;
        page.SetCompactMonth(compact);
        Surface.SelectedDate = compact ? _monthSelection : null;
        if (compact) ReloadMonthList();
    }

    // ------------------------------------------------------------ title

    /// <summary>
    /// The leading date title. Only the button's text changes, never the item's Title, so the bar does not lay out its
    /// buttons again while paging.
    /// </summary>
    private void UpdateTitle(VisibleDateRange? range = null)
    {
        if (_titleButton is not { } button) return;
        var title = TitleText(range ?? _viewportRange ?? _range);
        bool open = _panel?.IsOpen == true;
        UIView.PerformWithoutAnimation(() =>
        {
            var configuration = button.Configuration ?? UIButtonConfiguration.PlainButtonConfiguration;
            configuration.AttributedTitle = title;
            configuration.Image = WinoIcons.Image(open ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, 11, WinoStyle.Accent);
            button.Configuration = configuration;
            button.AccessibilityLabel = title.Value;
            // The bar sizes the custom view from its intrinsic size, so a longer title is not cut to the first one.
            button.InvalidateIntrinsicContentSize();
            button.SizeToFit();
            button.LayoutIfNeeded();
        });
    }

    /// <summary>While the surface's rail moves, the title, the strip and the Today capsule follow the period nearest to the viewport.</summary>
    protected override void ViewportPeriodChanged(CalendarDisplayType displayType, DateOnly anchorDate)
    {
        if (ViewModel.CurrentSettings is not { } settings) return;
        var range = CalendarRangeResolver.Resolve(new CalendarDisplayRequest(displayType, anchorDate), settings, DateContext.GetToday());
        _viewportRange = range;
        UpdateTitle(range);
        if (displayType == CalendarDisplayType.Day) SyncStrip(anchorDate, feedback: true);
        UpdateTodayCapsule();
    }

    private NSAttributedString TitleText(VisibleDateRange? range)
    {
        var font = WinoStyle.Fixed(20, UIFontWeight.Semibold);
        var primary = new UIStringAttributes { Font = font, ForegroundColor = UIColor.Label };
        if (range is null) return new NSAttributedString(Translator.KeyboardShortcuts_ModeCalendar, primary);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        static DateTime At(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);
        int thisYear = DateContext.GetToday().Year;
        switch (_displayType)
        {
            case CalendarDisplayType.Day:
                var day = range.AnchorDate;
                return new NSAttributedString(At(day).ToString(day.Year == thisYear ? "ddd, MMM d" : "ddd, MMM d, yyyy", culture), primary);
            case CalendarDisplayType.Month:
                var text = new NSMutableAttributedString(At(range.AnchorDate).ToString("MMMM", culture), primary);
                text.Append(new NSAttributedString(" " + range.AnchorDate.Year.ToString(culture),
                    new UIStringAttributes { Font = WinoStyle.Fixed(20), ForegroundColor = UIColor.SecondaryLabel }));
                return text;
            default:
                var dates = range.Dates;
                if (dates.Count == 0) return new NSAttributedString(At(range.AnchorDate).ToString("MMMM yyyy", culture), primary);
                var first = dates[0];
                var last = dates[^1];
                string span = first.Month == last.Month
                    ? $"{At(first).ToString("MMM d", culture)} – {last.Day.ToString(culture)}"
                    : $"{At(first).ToString("MMM d", culture)} – {At(last).ToString("MMM d", culture)}";
                if (last.Year != thisYear) span += ", " + last.Year.ToString(culture);
                return new NSAttributedString(span, primary);
        }
    }

    /// <summary>The leading title: the date with an accent chevron; a tap opens or closes the date selector.</summary>
    private UIBarButtonItem BuildTitleItem()
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.ImagePlacement = NSDirectionalRectEdge.Trailing;
        configuration.ImagePadding = 5;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 4, 6, 8);
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        var button = new UIButton { Configuration = configuration };
        button.AccessibilityHint = Translator.IOS_Calendar_ChooseDate;
        button.PrimaryActionTriggered += (_, _) => TogglePanel();
        _titleButton = button;
        UpdateTitle();
        var item = new UIBarButtonItem(button);
        if (OperatingSystem.IsIOSVersionAtLeast(26)) item.HidesSharedBackground = true;
        return item;
    }

    private void TogglePanel()
    {
        if (_panel is not { } panel) return;
        if (panel.IsOpen) panel.Close();
        else panel.Open();
    }

    private void ClosePanel() => _panel?.Close();

    // ------------------------------------------------------------ view menu

    private UIMenu BuildViewMenu()
    {
        var periods = CalendarPage.DisplayTypes.Select(type => (UIMenuElement)PhoneBarItems.Action(CalendarPage.TitleOf(type), CalendarPage.GlyphOf(type),
            () => PickView(type), on: type == _displayType || (type == CalendarDisplayType.Week && _displayType == CalendarDisplayType.WorkWeek && !CalendarPage.DisplayTypes.Contains(CalendarDisplayType.WorkWeek)))).ToArray();
        var list = PhoneBarItems.Action(Translator.IOS_Calendar_ShowAsList, WinoIconGlyph.CalendarAgenda, () => SetAgenda(!_agendaOn), on: _agendaOn);
        var calendars = PhoneBarItems.Action(Translator.IOS_Calendar_Calendars, WinoIconGlyph.List, OpenCalendars);
        return UIMenu.Create(string.Empty, [PhoneBarItems.Inline(periods), PhoneBarItems.Inline(list), PhoneBarItems.Inline(calendars)]);
    }

    private void UpdateViewMenu()
    {
        if (_viewItem is not { } item) return;
        item.Image = WinoIcons.TemplateImage(_agendaOn ? WinoIconGlyph.CalendarAgenda : CalendarPage.GlyphOf(_displayType), 22);
        item.Menu = BuildViewMenu();
    }

    private void PickView(CalendarDisplayType type)
    {
        ClosePanel();
        RequestDisplayType(type);
    }

    private void OpenCalendars()
    {
        ClosePanel();
        PhoneShellViewController.Of(this)?.OpenDrawer();
    }

    /// <summary>Drill-down into Day for <paramref name="date"/>; the way back is the view menu.</summary>
    private void OpenDay(DateOnly date)
    {
        ClosePanel();
        _stripSelection = date;
        GoToDate(date);
        RequestDisplayType(CalendarDisplayType.Day);
    }

    private void SurfaceDayOpenRequested(object? sender, DateOnly date) => OpenDay(date);

    /// <summary>A pinch on the hour grid saves the hour height as the shared calendar preference.</summary>
    private void SurfaceHourHeightChanged(object? sender, double height)
    {
        if (Services.GetService(typeof(IPreferencesService)) is IPreferencesService preferences) preferences.HourHeight = height;
    }

    // ------------------------------------------------------------ week strip

    /// <summary>The strip shows the week of the selected day: the Day view's day, or in the list the picked day of the week.</summary>
    private void SyncStrip(DateOnly? selected, bool feedback)
    {
        if (_page is not { } page) return;
        var today = DateContext.GetToday();
        if (selected is { } day) _stripSelection = day;
        var range = _viewportRange ?? _range;
        if (_displayType == CalendarDisplayType.Day && selected is null && range is not null) _stripSelection = range.AnchorDate;
        else if (_displayType != CalendarDisplayType.Day && range is not null && (_stripSelection is not { } kept || !range.Contains(kept)))
            _stripSelection = range.Contains(today) ? today : range.StartDate;
        page.Strip.Update(_stripSelection ?? today, today, _firstDayOfWeek, feedback);
    }

    private void StripDateTapped(DateOnly date)
    {
        ClosePanel();
        if (_agendaOn && ScrollAgendaTo(date))
        {
            SyncStrip(date, feedback: true);
            return;
        }
        SyncStrip(date, feedback: true);
        GoToDate(date);
    }

    // ------------------------------------------------------------ month selection

    /// <summary>
    /// The compact month selects a day: after a picked date, the picked day; otherwise today when the month holds it,
    /// else the 1st (a new month), keeping a selection that is still inside the month.
    /// </summary>
    private void SettleMonthSelection(VisibleDateRange range)
    {
        if (range.DisplayType != CalendarDisplayType.Month) return;
        var today = DateContext.GetToday();
        DateOnly selection;
        if (_pendingMonthSelection is { } pending && range.Contains(pending)) selection = pending;
        else if (_monthSelection is { } current && range.Contains(current)) selection = current;
        else selection = range.Contains(today) ? today : range.StartDate;
        _pendingMonthSelection = null;
        SelectMonthDay(selection, feedback: false);
    }

    private void SelectMonthDay(DateOnly date, bool feedback)
    {
        bool changed = _monthSelection != date;
        _monthSelection = date;
        if (_page?.IsCompactMonth == true) Surface.SelectedDate = date;
        if (!changed) return;
        if (feedback) new UISelectionFeedbackGenerator().SelectionChanged();
        ReloadMonthList();
    }

    /// <summary>A tap on a compact month cell selects the day; the base class ignores taps without a range on the phone.</summary>
    private void SurfaceSlotSelected(object? sender, CalendarSlotClickedEventArgs args)
    {
        if (args.End is not null || _displayType != CalendarDisplayType.Month || _page?.IsCompactMonth != true) return;
        var date = DateOnly.FromDateTime(args.Start);
        // A day of the neighbour month glides there; the selection follows once that month settles.
        if (_range is { } range && !range.Contains(date))
        {
            _pendingMonthSelection = date;
            GoToDate(date);
            return;
        }
        SelectMonthDay(date, feedback: true);
    }

    // ------------------------------------------------------------ Today capsule

    /// <summary>The capsule shows while the visible (or moving) range does not hold today, pointing to it.</summary>
    private void UpdateTodayCapsule()
    {
        if (_capsule is not { } capsule) return;
        var range = _viewportRange ?? _range;
        var today = DateContext.GetToday();
        int direction = range is null || range.Contains(today) ? 0 : today < range.StartDate ? -1 : 1;
        if (_panel?.IsOpen == true || ViewModel.Readiness.IsBlocked) direction = 0;
        capsule.Show(direction, _displayType == CalendarDisplayType.Month && !_agendaOn);
        View?.SetNeedsLayout();
    }

    private void TodayCapsuleTapped()
    {
        if (_displayType == CalendarDisplayType.Month) _pendingMonthSelection = DateContext.GetToday();
        GoToday();
    }

    /// <summary>The shell's floating button: New event.</summary>
    void IPhoneFloatingActionPage.FloatingActionInvoked() => NewEvent();

    CalendarDisplayType ICalendarPickerHost.DisplayType => _displayType;
    VisibleDateRange? ICalendarPickerHost.VisibleRange => _range;
    DayOfWeek ICalendarPickerHost.FirstDayOfWeek => _firstDayOfWeek;
    void ICalendarPickerHost.PickToday()
    {
        if (_displayType == CalendarDisplayType.Month) _pendingMonthSelection = DateContext.GetToday();
        GoToday();
        ClosePanel();
    }
    void ICalendarPickerHost.PickDate(DateOnly date)
    {
        if (_displayType == CalendarDisplayType.Month)
        {
            if (_range is { } range && range.Contains(date)) SelectMonthDay(date, feedback: true);
            else _pendingMonthSelection = date;
        }
        // In the agenda a picked day scrolls to its header when the list holds it.
        if (!_agendaOn || !ScrollAgendaTo(date)) GoToDate(date);
        ClosePanel();
    }
    void ICalendarPickerHost.ClosePicker() => ClosePanel();
    void ICalendarPickerHost.PickerVisibilityChanged(bool open)
    {
        UpdateTitle();
        UpdateTodayCapsule();
    }

    protected override void ReadinessChanged(bool blocked) => ApplyLayout(animated: false);

    private void SetAgenda(bool on)
    {
        if (_agendaOn == on) return;
        _agendaOn = on;
        if (_page is { } page && page.Window is not null)
            UIView.Transition(page.Body, 0.2, UIViewAnimationOptions.TransitionCrossDissolve | UIViewAnimationOptions.AllowAnimatedContent, null, null);
        ApplyLayout(animated: true);
        if (on) ReloadAgenda();
        UpdateTodayCapsule();
    }

    private void ApplyContentVisibility()
    {
        bool blocked = ViewModel.Readiness.IsBlocked;
        Surface.Hidden = blocked || _agendaOn;
        SetAgendaHidden(blocked || !_agendaOn);
        SetMonthListHidden(blocked || _agendaOn || _page?.IsCompactMonth != true);
    }

    /// <summary>The event composer this page took from the rendering frame: a full-screen sheet.</summary>
    protected override bool ShowComposer(CalendarEventComposePageViewController composer)
        => CalendarComposerSheet.Present(_host, Services, composer);

    /// <summary>A composer sheet still open when the page leaves closes with it (its draft state is the composer's).</summary>
    protected override Task ReleaseHostedPagesAsync()
        => CalendarComposerSheet.Current is { } sheet ? sheet.CloseAsync() : Task.CompletedTask;

    protected override void ReleaseIdiom()
    {
        Surface.DayOpenRequested -= SurfaceDayOpenRequested;
        Surface.SlotClicked -= SurfaceSlotSelected;
        Surface.HourHeightChanged -= SurfaceHourHeightChanged;
        ReleaseAgenda();
        ReleaseSearch();
        ClosePanel();
    }
}
