using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>What the date panel asks of the calendar page.</summary>
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
}

/// <summary>
/// The iPhone calendar's date panel (concept B of 18 Oct 2026, without the calendar chips: the drawer picks calendars).
/// It drops from the bar title over the grid: Day · Week · Month with the agenda toggle, the phone month grid
/// (<see cref="CalendarMonthPickerView"/>) with the visible range, and Today. Picking a day or Today goes there and closes
/// the panel; the period and the agenda toggle keep it open (Android). A tap outside the panel closes it.
/// </summary>
internal sealed class CalendarDatePanelView : UIView
{
    private const double Inset = 12;

    private readonly ICalendarPickerHost _host;
    private readonly UIView _scrim = new() { BackgroundColor = UIColor.Black.ColorWithAlpha(0.12f) };
    private readonly UIVisualEffectView _panel = new(UIBlurEffect.FromStyle(UIBlurEffectStyle.SystemThickMaterial));
    private readonly UISegmentedControl _period = new(CalendarPage.DisplayTypeTitles);
    private readonly UIButton _agenda = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly CalendarMonthPickerView _month = new();

    public CalendarDatePanelView(ICalendarPickerHost host)
    {
        _host = host;
        TranslatesAutoresizingMaskIntoConstraints = false;
        Hidden = true;
        _scrim.TranslatesAutoresizingMaskIntoConstraints = false;
        _scrim.AddGestureRecognizer(new UITapGestureRecognizer(() => host.ClosePicker()));
        _scrim.IsAccessibilityElement = true;
        _scrim.AccessibilityLabel = Translator.Buttons_Close;
        _scrim.AccessibilityTraits = UIAccessibilityTrait.Button;
        WinoLayout.Fill(_scrim, this);

        _panel.TranslatesAutoresizingMaskIntoConstraints = false;
        _panel.Layer.CornerRadius = 18;
        _panel.Layer.CornerCurve = CoreAnimation.CACornerCurve.Continuous;
        _panel.ClipsToBounds = true;
        _panel.AccessibilityViewIsModal = true;

        _period.TranslatesAutoresizingMaskIntoConstraints = false;
        _period.ValueChanged += (_, _) =>
        {
            var index = (int)_period.SelectedSegment;
            if (index >= 0 && index < CalendarPage.DisplayTypes.Length) host.PickDisplayType(CalendarPage.DisplayTypes[index]);
        };
        _period.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        _agenda.PrimaryActionTriggered += (_, _) =>
        {
            host.PickAgenda(!host.IsAgendaOn);
            Refresh();
        };
        WinoLayout.Size(_agenda, 44, 32);
        WinoAccessibility.Label(_agenda, Translator.IOS_Calendar_Agenda);

        _month.DateClicked += (_, date) => host.PickDate(date);

        var todayConfiguration = UIButtonConfiguration.GrayButtonConfiguration;
        todayConfiguration.Title = Translator.Today;
        todayConfiguration.Image = WinoIcons.TemplateImage(WinoIconGlyph.CalendarToday, 16);
        todayConfiguration.ImagePadding = 6;
        todayConfiguration.CornerStyle = UIButtonConfigurationCornerStyle.Capsule;
        var today = new UIButton { Configuration = todayConfiguration, TranslatesAutoresizingMaskIntoConstraints = false };
        today.PrimaryActionTriggered += (_, _) => host.PickToday();

        var row = WinoLayout.HStack(8, _period, _agenda);
        row.Alignment = UIStackViewAlignment.Center;
        var footer = WinoLayout.HStack(0, today, WinoLayout.Spacer());
        footer.Alignment = UIStackViewAlignment.Center;
        var column = WinoLayout.VStack(12, row, _month, footer);
        column.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(column, _panel.ContentView, 14, 14, 12, 14);
        AddSubview(_panel);
        NSLayoutConstraint.ActivateConstraints(
        [
            _panel.TopAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.TopAnchor, 6),
            _panel.LeadingAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.LeadingAnchor, (nfloat)Inset),
            _panel.TrailingAnchor.ConstraintEqualTo(SafeAreaLayoutGuide.TrailingAnchor, -(nfloat)Inset)
        ]);
        Refresh();
    }

    public bool IsOpen => !Hidden;

    /// <summary>Drops the panel from the bar (a fade under Reduce Motion).</summary>
    public void Open()
    {
        if (!Hidden) return;
        Refresh();
        Hidden = false;
        Superview?.BringSubviewToFront(this);
        LayoutIfNeeded();
        _scrim.Alpha = 0;
        _panel.Alpha = 0;
        if (!UIAccessibility.IsReduceMotionEnabled)
            _panel.Transform = CGAffineTransform.Multiply(CGAffineTransform.MakeScale(0.96f, 0.9f), CGAffineTransform.MakeTranslation(0, -24));
        UIView.AnimateNotify(0.32, 0, 0.86f, 0, UIViewAnimationOptions.AllowUserInteraction, () =>
        {
            _scrim.Alpha = 1;
            _panel.Alpha = 1;
            _panel.Transform = CGAffineTransform.MakeIdentity();
        }, null);
        UIAccessibility.PostNotification(UIAccessibilityPostNotification.ScreenChanged, _panel);
    }

    public void Close()
    {
        if (Hidden) return;
        UIView.Animate(0.18, 0, UIViewAnimationOptions.BeginFromCurrentState, () =>
        {
            _scrim.Alpha = 0;
            _panel.Alpha = 0;
            if (!UIAccessibility.IsReduceMotionEnabled) _panel.Transform = CGAffineTransform.MakeTranslation(0, -16);
        }, () =>
        {
            Hidden = true;
            _panel.Transform = CGAffineTransform.MakeIdentity();
            UIAccessibility.PostNotification(UIAccessibilityPostNotification.ScreenChanged, null);
        });
    }

    public override bool AccessibilityPerformEscape()
    {
        _host.ClosePicker();
        return true;
    }

    /// <summary>Reads the page state again (the range, period or agenda changed).</summary>
    public void Refresh()
    {
        _period.SelectedSegment = CalendarPage.SegmentFor(_host.DisplayType);
        bool on = _host.IsAgendaOn;
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(WinoIconGlyph.CalendarAgenda, 18, on ? WinoStyle.Accent : WinoStyle.SecondaryText);
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Capsule;
        configuration.Background.BackgroundColor = on ? WinoStyle.Accent.ColorWithAlpha(0.15f) : UIColor.Clear;
        _agenda.Configuration = configuration;
        _agenda.Selected = on;
        _month.FirstDayOfWeek = _host.FirstDayOfWeek;
        if (_host.VisibleRange is { } range)
        {
            _month.SelectedDates = new HashSet<DateOnly>(range.Dates);
            _month.DisplayMonth = range.AnchorDate;
        }
    }
}
