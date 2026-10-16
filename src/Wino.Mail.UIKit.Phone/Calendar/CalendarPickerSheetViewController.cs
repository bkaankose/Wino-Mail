using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>What the date picker sheet asks of the calendar page.</summary>
internal interface ICalendarPickerHost
{
    CalendarDisplayType DisplayType { get; }
    bool IsAgendaOn { get; }
    VisibleDateRange? VisibleRange { get; }
    DayOfWeek FirstDayOfWeek { get; }
    void PickDisplayType(CalendarDisplayType type);
    void PickAgenda(bool on);
    void PickToday();
    void PickDate(DateOnly date);
    void ClosePicker();
    void PickCalendars();
}

/// <summary>
/// The iPhone calendar's date picker sheet (option B, 15 Oct 2026), opened from the bar title: Day · Week · Month, the
/// agenda toggle and the calendars filter in one row, then the Wino month grid with the visible range highlighted. Picking a day goes there and
/// closes the sheet; the period, agenda and Today keep it open. Today and Done sit in the sheet's bar.
/// </summary>
internal sealed class CalendarPickerSheetViewController(ICalendarPickerHost host) : UIViewController
{
    private UISegmentedControl? _period;
    private UIButton? _agenda;
    private WinoMiniCalendarView? _month;

    public override void LoadView()
    {
        NavigationItem.LeftBarButtonItem = new UIBarButtonItem(Translator.Today, UIBarButtonItemStyle.Plain, (_, _) => host.PickToday());
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => host.ClosePicker());

        var period = new UISegmentedControl([Translator.CalendarTypeSelector_Day, Translator.CalendarTypeSelector_Week, Translator.CalendarTypeSelector_Month])
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        period.ValueChanged += (_, _) =>
        {
            var index = (int)period.SelectedSegment;
            if (index >= 0 && index < CalendarPage.DisplayTypes.Length) host.PickDisplayType(CalendarPage.DisplayTypes[index]);
        };
        period.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        _period = period;

        var agenda = new UIButton { TranslatesAutoresizingMaskIntoConstraints = false };
        agenda.PrimaryActionTriggered += (_, _) =>
        {
            host.PickAgenda(!host.IsAgendaOn);
            Refresh();
        };
        WinoLayout.Size(agenda, 44, 36);
        WinoAccessibility.Label(agenda, Translator.IOS_Calendar_Agenda);
        _agenda = agenda;

        var month = new WinoMiniCalendarView { Collapsed = false };
        month.SetToggle(WinoIconGlyph.Dismiss, Translator.Buttons_Close);
        month.ToggleClicked += (_, _) => host.ClosePicker();
        month.DateClicked += (_, date) => host.PickDate(date);
        _month = month;

        var calendars = new UIButton { TranslatesAutoresizingMaskIntoConstraints = false };
        var calendarsConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        calendarsConfiguration.Image = WinoIcons.Image(WinoIconGlyph.Filter, 18, WinoStyle.SecondaryText);
        calendarsConfiguration.CornerStyle = UIButtonConfigurationCornerStyle.Capsule;
        calendars.Configuration = calendarsConfiguration;
        calendars.PrimaryActionTriggered += (_, _) => host.PickCalendars();
        WinoLayout.Size(calendars, 44, 36);
        WinoAccessibility.Label(calendars, Translator.IOS_Calendar_Calendars);

        var row = WinoLayout.HStack(4, period, agenda, calendars);
        row.Alignment = UIStackViewAlignment.Center;
        var column = WinoLayout.VStack(12, row, month);
        column.Alignment = UIStackViewAlignment.Fill;
        var root = new UIView { BackgroundColor = UIColor.SystemGroupedBackground };
        root.AddSubview(column);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 8),
            column.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, 16),
            column.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -16),
            column.BottomAnchor.ConstraintLessThanOrEqualTo(guide.BottomAnchor, -8)
        ]);
        View = root;
        Refresh();
    }

    /// <summary>Reads the page state again (the range or period changed while the sheet is open).</summary>
    public void Refresh()
    {
        if (_period is { } period) period.SelectedSegment = CalendarPage.SegmentFor(host.DisplayType);
        if (_agenda is { } agenda)
        {
            bool on = host.IsAgendaOn;
            var configuration = UIButtonConfiguration.PlainButtonConfiguration;
            configuration.Image = WinoIcons.Image(WinoIconGlyph.CalendarAgenda, 18, on ? WinoStyle.Accent : WinoStyle.SecondaryText);
            configuration.CornerStyle = UIButtonConfigurationCornerStyle.Capsule;
            configuration.Background.BackgroundColor = on ? WinoStyle.Accent.ColorWithAlpha(0.15f) : UIColor.Clear;
            agenda.Configuration = configuration;
            agenda.Selected = on;
        }
        if (_month is { } month)
        {
            month.FirstDayOfWeek = host.FirstDayOfWeek;
            if (host.VisibleRange is { } range)
            {
                month.SelectedDates = new HashSet<DateOnly>(range.Dates);
                month.DisplayMonth = range.AnchorDate;
            }
        }
    }
}
