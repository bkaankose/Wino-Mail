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
using Wino.Mail.UIKit.Phone.Shell;
using Wino.Mail.UIKit.Views.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The iPhone Calendar tab (iOS design decisions; concept B of 18 Oct 2026): the shell's account avatar leading, the date
/// as a one-line title ("Sun, Oct 18", "Oct 12 – 18", "October 2026") and the event search glyph trailing; New event is
/// the shell's floating button. The title drops the date panel over the grid (<see cref="CalendarDatePanelView"/>:
/// Day · Week · Month, the agenda toggle, the month grid and Today); calendars are picked in the shell drawer. The content
/// zone holds the Windows timed surface (Day without its own column header, Week and Month with theirs) or the agenda
/// list, and a horizontal drag pages the period with the finger (<see cref="CalendarPagingInteraction"/>). A tile tap
/// pushes the event details, the composer opens as a full-screen sheet, and a long press on empty time opens the quick
/// event sheet; a long press on a tile moves it (or resizes it from its bottom edge).
/// Partial files: Agenda (the list), Search (the event search), Debug (bridge commands).
/// </summary>
public sealed partial class CalendarPageViewController : CalendarPageViewControllerBase, ICalendarPickerHost, IPhoneFloatingActionPage
{
    private readonly PhoneShellHost _host;
    private CalendarPage? _page;
    private bool _agendaOn;
    private CalendarDatePanelView? _panel;
    private UIButton? _titleButton;
    private CalendarPagingInteraction? _paging;
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
        PlaceSurface(page.Body);
        BuildAgenda(page.Body);
        ApplySurfaceHeader();

        _paging = new CalendarPagingInteraction(page.Body, () => !Surface.IsDraggingItem && !Surface.IsSelectingRange && !ViewModel.Readiness.IsBlocked && _panel?.IsOpen != true,
            direction => { if (direction > 0) GoNext(); else GoPrevious(); });

        var panel = new CalendarDatePanelView(this);
        _panel = panel;
        WinoLayout.Fill(panel, page);

        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        NavigationItem.TitleView = BuildTitleButton();
        BuildSearch();
        View = page;
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
        _panel?.Refresh();
        UpdateTitle();
        if (range is not null) ReloadAgenda();
    }

    protected override void SettingsChanged(CalendarSettings? settings)
    {
        if (settings is null) return;
        _firstDayOfWeek = settings.FirstDayOfWeek;
        _panel?.Refresh();
    }

    protected override void ItemsChanged()
    {
        ObserveAgendaItems();
        ReloadAgenda();
    }

    protected override void DisplayTypeChanged(CalendarDisplayType type)
    {
        _displayType = type;
        ApplySurfaceHeader();
        UpdateTitle();
        _panel?.Refresh();
    }

    /// <summary>
    /// The one-line date title: the day, the week's range or the month. Only the title view's text changes, never the
    /// item's Title, so the bar does not lay out its buttons again while paging.
    /// </summary>
    private void UpdateTitle()
    {
        var title = TitleText();
        if (_titleButton is not { } button)
        {
            NavigationItem.Title = title;
            return;
        }
        UIView.PerformWithoutAnimation(() =>
        {
            var configuration = button.Configuration ?? UIButtonConfiguration.PlainButtonConfiguration;
            configuration.AttributedTitle = new Foundation.NSAttributedString(title, new UIStringAttributes { Font = WinoStyle.Fixed(17, UIFontWeight.Semibold), ForegroundColor = UIColor.Label });
            button.Configuration = configuration;
            button.AccessibilityLabel = title;
            // The bar sizes an Auto Layout title view from its intrinsic size, so a longer title is not cut to the first one.
            button.InvalidateIntrinsicContentSize();
            button.LayoutIfNeeded();
        });
    }

    private string TitleText()
    {
        if (_range is not { } range) return Translator.KeyboardShortcuts_ModeCalendar;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        static DateTime At(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);
        switch (_displayType)
        {
            case CalendarDisplayType.Day:
                return At(range.AnchorDate).ToString("ddd, MMM d", culture);
            case CalendarDisplayType.Month:
                return At(range.AnchorDate).ToString("MMMM yyyy", culture);
            default:
                var dates = range.Dates.ToList();
                if (dates.Count == 0) return At(range.AnchorDate).ToString("MMMM yyyy", culture);
                var first = dates[0];
                var last = dates[^1];
                return first.Month == last.Month
                    ? $"{At(first).ToString("MMM d", culture)} – {last.Day.ToString(culture)}"
                    : $"{At(first).ToString("MMM d", culture)} – {At(last).ToString("MMM d", culture)}";
        }
    }

    /// <summary>The bar title: the date with a chevron; a tap drops the date panel or closes it.</summary>
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
        button.PrimaryActionTriggered += (_, _) => TogglePanel();
        _titleButton = button;
        UpdateTitle();
        return button;
    }

    private void TogglePanel()
    {
        if (_panel is not { } panel) return;
        if (panel.IsOpen) panel.Close();
        else panel.Open();
    }

    private void ClosePanel() => _panel?.Close();

    /// <summary>The shell's floating button: New event.</summary>
    void IPhoneFloatingActionPage.FloatingActionInvoked() => NewEvent();

    CalendarDisplayType ICalendarPickerHost.DisplayType => _displayType;
    bool ICalendarPickerHost.IsAgendaOn => _agendaOn;
    VisibleDateRange? ICalendarPickerHost.VisibleRange => _range;
    DayOfWeek ICalendarPickerHost.FirstDayOfWeek => _firstDayOfWeek;
    void ICalendarPickerHost.PickDisplayType(CalendarDisplayType type) => RequestDisplayType(type);
    void ICalendarPickerHost.PickAgenda(bool on) => SetAgenda(on);
    void ICalendarPickerHost.PickToday()
    {
        GoToday();
        ClosePanel();
    }
    void ICalendarPickerHost.PickDate(DateOnly date)
    {
        // In the agenda a picked day scrolls to its header when the list holds it.
        if (!_agendaOn || !ScrollAgendaTo(date)) GoToDate(date);
        ClosePanel();
    }
    void ICalendarPickerHost.ClosePicker() => ClosePanel();

    protected override void ReadinessChanged(bool blocked) => ApplyContentVisibility();

    /// <summary>Day shows no column header (the title names the day); Week and Month keep theirs, which page with the grid.</summary>
    private void ApplySurfaceHeader() => Surface.ShowsDayHeader = _displayType != CalendarDisplayType.Day;

    private void SetAgenda(bool on)
    {
        _agendaOn = on;
        ApplyContentVisibility();
        if (on) ReloadAgenda();
    }

    private void ApplyContentVisibility()
    {
        bool blocked = ViewModel.Readiness.IsBlocked;
        Surface.Hidden = blocked || _agendaOn;
        SetAgendaHidden(blocked || !_agendaOn);
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
        ReleaseSearch();
        ClosePanel();
    }
}
