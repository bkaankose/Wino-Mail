using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Mail.UIKit.Views.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The iPhone Calendar tab's root view over the wallpaper: a header zone with the collapsible month picker
/// (<see cref="WinoMiniCalendarView"/>, the visible range highlighted) and the period row (previous, Day · Week · Month,
/// next, the agenda toggle), then the content zone that holds the timed surface or the agenda list.
/// </summary>
public sealed class CalendarPage : UIView
{
    public static readonly CalendarDisplayType[] DisplayTypes = [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.Month];

    public CalendarPage()
    {
        BackgroundColor = UIColor.Clear;
        HeaderZone = new WinoZoneView();
        ContentZone = new WinoZoneView();
        AddSubview(HeaderZone);
        AddSubview(ContentZone);

        MiniCalendar = new WinoMiniCalendarView();
        Period = new UISegmentedControl([Translator.CalendarTypeSelector_Day, Translator.CalendarTypeSelector_Week, Translator.CalendarTypeSelector_Month])
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        Previous = IconButton(WinoIconGlyph.ArrowLeft, Translator.Accessibility_PreviousCalendarPeriod);
        Next = IconButton(WinoIconGlyph.ArrowRight, Translator.Accessibility_NextCalendarPeriod);
        Agenda = IconButton(WinoIconGlyph.CalendarAgenda, Translator.IOS_Calendar_Agenda);
        Period.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        var periodRow = WinoLayout.HStack(6, Previous, Period, Next, Agenda);
        periodRow.Alignment = UIStackViewAlignment.Center;

        var header = WinoLayout.Insets(WinoLayout.VStack(6, MiniCalendar, periodRow), 4, 8, 8, 8);
        header.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(header, HeaderZone.ContentView);

        var guide = SafeAreaLayoutGuide;
        var gutter = (nfloat)WinoStyle.ZoneGutter;
        NSLayoutConstraint.ActivateConstraints(
        [
            HeaderZone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, gutter),
            HeaderZone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, gutter),
            HeaderZone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -gutter),
            ContentZone.TopAnchor.ConstraintEqualTo(HeaderZone.BottomAnchor, gutter),
            ContentZone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, gutter),
            ContentZone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -gutter),
            ContentZone.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -gutter)
        ]);
    }

    public WinoZoneView HeaderZone { get; }
    public WinoZoneView ContentZone { get; }
    public WinoMiniCalendarView MiniCalendar { get; }
    public UISegmentedControl Period { get; }
    public UIButton Previous { get; }
    public UIButton Next { get; }
    public UIButton Agenda { get; }

    /// <summary>The segment of a display type; Work week shows as Week on the phone.</summary>
    public static int SegmentFor(CalendarDisplayType type) => type switch
    {
        CalendarDisplayType.Day => 0,
        CalendarDisplayType.Month => 2,
        _ => 1
    };

    public void SetAgendaOn(bool on)
    {
        var configuration = Agenda.Configuration ?? UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(WinoIconGlyph.CalendarAgenda, 18, on ? WinoStyle.Accent : WinoStyle.SecondaryText);
        configuration.Background.BackgroundColor = on ? WinoStyle.Accent.ColorWithAlpha(0.15f) : UIColor.Clear;
        Agenda.Configuration = configuration;
        Agenda.Selected = on;
    }

    private static UIButton IconButton(WinoIconGlyph glyph, string label)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, 18, WinoStyle.SecondaryText);
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Capsule;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(button, 40, 36);
        WinoAccessibility.Label(button, label);
        return button;
    }
}
