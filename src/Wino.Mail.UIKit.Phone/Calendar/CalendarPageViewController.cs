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
/// The iPhone Calendar tab (iOS design decisions, date picker option B of 15 Oct 2026): the range as a tappable title
/// with Today and New event in the bar; the title opens the picker sheet (Day · Week · Month, the agenda toggle,
/// the calendars filter, Today and the month grid). Day view shows a week strip above the content zone; the content zone holds
/// the Windows timed surface (one column for Day) or the agenda list, and a horizontal swipe pages the period.
/// A tile tap pushes the event details, the composer opens as a full-screen sheet, and a long press on empty time
/// opens the quick event sheet; a long press on a tile moves it (or resizes it from its bottom edge).
/// Partial files: Agenda (the list), Debug (bridge commands).
/// </summary>
public sealed partial class CalendarPageViewController : CalendarPageViewControllerBase, ICalendarPickerHost
{
    private readonly PhoneShellHost _host;
    private CalendarPage? _page;
    private UINavigationController? _calendarsSheet;
    private bool _agendaOn;
    private UINavigationController? _pickerSheet;
    private CalendarPickerSheetViewController? _picker;
    private UIButton? _titleButton;
    private CalendarDisplayType _displayType = CalendarDisplayType.Week;
    private VisibleDateRange? _range;
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
        PlaceSurface(page.ContentZone.ContentView);
        BuildAgenda(page.ContentZone.ContentView);

        page.WeekStrip.DateClicked += (_, date) =>
        {
            // In the agenda a strip tap jumps to that day's header when the list holds it.
            if (_agendaOn && ScrollAgendaTo(date)) page.WeekStrip.SelectedDate = date;
            else GoToDate(date);
        };
        page.SetStripVisible(false);

        // A horizontal swipe on the surface pages the period, like the iOS Calendar day view.
        Surface.AddGestureRecognizer(new UISwipeGestureRecognizer(GoNext) { Direction = UISwipeGestureRecognizerDirection.Left });
        Surface.AddGestureRecognizer(new UISwipeGestureRecognizer(GoPrevious) { Direction = UISwipeGestureRecognizerDirection.Right });

        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        NavigationItem.TitleView = BuildTitleButton();
        var today = new UIBarButtonItem(Translator.Today, UIBarButtonItemStyle.Plain, (_, _) => GoToday());
        NavigationItem.LeftBarButtonItem = today;
        View = page;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        // The shell placed its Settings button before activation; New event joins it (the calendars filter is in the picker).
        var items = new List<UIBarButtonItem>();
        if (NavigationItem.RightBarButtonItem is { } avatar) items.Add(avatar);
        items.Add(PhoneBarItems.Button(WinoIconGlyph.Add, Translator.CalendarEventCompose_NewEventButton, NewEvent));
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
        _range = range;
        _picker?.Refresh();
        if (_page is not { } page || range is null) return;
        page.WeekStrip.SelectedDate = range.AnchorDate;
        ReloadAgenda();
    }

    protected override void SettingsChanged(CalendarSettings? settings)
    {
        if (settings is null) return;
        _firstDayOfWeek = settings.FirstDayOfWeek;
        if (_page is { } page) page.WeekStrip.FirstDayOfWeek = settings.FirstDayOfWeek;
        _picker?.Refresh();
    }

    protected override void ItemsChanged()
    {
        ObserveAgendaItems();
        ReloadAgenda();
    }

    protected override void DisplayTypeChanged(CalendarDisplayType type)
    {
        _displayType = type;
        ApplyStripVisibility();
        _picker?.Refresh();
    }

    protected override void RangeTextChanged(string? text)
    {
        var title = string.IsNullOrWhiteSpace(text) ? Translator.KeyboardShortcuts_ModeCalendar : text;
        NavigationItem.Title = title;
        if (_titleButton is not { } button) return;
        var configuration = button.Configuration ?? UIButtonConfiguration.PlainButtonConfiguration;
        configuration.AttributedTitle = new Foundation.NSAttributedString(title, new UIStringAttributes { Font = WinoStyle.Fixed(17, UIFontWeight.Semibold), ForegroundColor = UIColor.Label });
        button.Configuration = configuration;
        button.AccessibilityLabel = title;
        // The bar sizes an Auto Layout title view from its intrinsic size, so a longer range is not cut to the first one.
        button.InvalidateIntrinsicContentSize();
    }

    /// <summary>The bar title: the range with a chevron; a tap opens the date picker sheet.</summary>
    private UIButton BuildTitleButton()
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(WinoIconGlyph.ChevronDown, 11, WinoStyle.Accent);
        configuration.ImagePlacement = NSDirectionalRectEdge.Trailing;
        configuration.ImagePadding = 4;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 8, 6, 8);
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.SetContentCompressionResistancePriority((float)UILayoutPriority.DefaultHigh, UILayoutConstraintAxis.Horizontal);
        button.AccessibilityHint = Translator.IOS_Calendar_ChooseDate;
        button.PrimaryActionTriggered += (_, _) => ShowPicker();
        _titleButton = button;
        RangeTextChanged(NavigationItem.Title);
        return button;
    }

    /// <summary>The date picker sheet (period, agenda, Today and the month grid).</summary>
    private void ShowPicker()
    {
        if (_pickerSheet is { PresentingViewController: not null }) return;
        var picker = new CalendarPickerSheetViewController(this);
        _picker = picker;
        _pickerSheet = _host.PresentSheet(picker, PhoneSheetSize.MediumAndLarge, () => { _pickerSheet = null; _picker = null; });
    }

    private void ClosePickerSheet()
    {
        _picker = null;
        if (_pickerSheet is { } sheet)
        {
            _pickerSheet = null;
            _ = _host.DismissSheetAsync(sheet);
        }
    }

    CalendarDisplayType ICalendarPickerHost.DisplayType => _displayType;
    bool ICalendarPickerHost.IsAgendaOn => _agendaOn;
    VisibleDateRange? ICalendarPickerHost.VisibleRange => _range;
    DayOfWeek ICalendarPickerHost.FirstDayOfWeek => _firstDayOfWeek;
    void ICalendarPickerHost.PickDisplayType(CalendarDisplayType type) => RequestDisplayType(type);
    void ICalendarPickerHost.PickAgenda(bool on) => SetAgenda(on);
    void ICalendarPickerHost.PickToday() => GoToday();
    void ICalendarPickerHost.PickDate(DateOnly date)
    {
        GoToDate(date);
        ClosePickerSheet();
    }
    void ICalendarPickerHost.ClosePicker() => ClosePickerSheet();
    void ICalendarPickerHost.PickCalendars()
    {
        // One sheet at a time: the calendars filter replaces the picker.
        if (_pickerSheet is { } sheet)
        {
            _picker = null;
            _pickerSheet = null;
            _ = ReplacePickerWithCalendarsAsync(sheet);
            return;
        }
        ShowCalendars();
    }

    protected override void ReadinessChanged(bool blocked) => ApplyContentVisibility();

    /// <summary>The week strip shows in Day view and over the agenda of Day and Week (Calendar board, 15 Oct 2026).</summary>
    private void ApplyStripVisibility()
        => _page?.SetStripVisible(_displayType == CalendarDisplayType.Day || _agendaOn && _displayType != CalendarDisplayType.Month);

    private void SetAgenda(bool on)
    {
        _agendaOn = on;
        ApplyStripVisibility();
        ApplyContentVisibility();
        if (on) ReloadAgenda();
    }

    private void ApplyContentVisibility()
    {
        bool blocked = ViewModel.Readiness.IsBlocked;
        Surface.Hidden = blocked || _agendaOn;
        SetAgendaHidden(blocked || !_agendaOn);
    }

    private async Task ReplacePickerWithCalendarsAsync(UINavigationController picker)
    {
        try
        {
            await _host.DismissSheetAsync(picker);
            ShowCalendars();
        }
        catch (Exception exception) { ReportError(exception); }
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
        ClosePickerSheet();
        if (_calendarsSheet is { } sheet)
        {
            _calendarsSheet = null;
            _ = _host.DismissSheetAsync(sheet);
        }
    }
}
