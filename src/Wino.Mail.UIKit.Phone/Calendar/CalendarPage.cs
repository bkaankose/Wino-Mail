using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The iPhone Calendar tab's root view over the wallpaper (date picker option B, 15 Oct 2026): in Day view a small zone
/// with the week strip (<see cref="CalendarWeekStripView"/>), then the content zone that holds the timed surface or the
/// agenda list. Week and Month views drop the strip zone so the grid starts right under the bar; the period, agenda
/// toggle and month picker live in the sheet the bar title opens (<see cref="CalendarPickerSheetViewController"/>).
/// </summary>
public sealed class CalendarPage : UIView
{
    public static readonly CalendarDisplayType[] DisplayTypes = [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.Month];

    private readonly NSLayoutConstraint _contentBelowStrip;
    private readonly NSLayoutConstraint _contentAtTop;

    public CalendarPage()
    {
        BackgroundColor = UIColor.Clear;
        StripZone = new WinoZoneView();
        ContentZone = new WinoZoneView();
        AddSubview(StripZone);
        AddSubview(ContentZone);

        WeekStrip = new CalendarWeekStripView();
        WinoLayout.Fill(WeekStrip, StripZone.ContentView, 0, 4, 0, 4);

        var guide = SafeAreaLayoutGuide;
        var gutter = (nfloat)WinoStyle.ZoneGutter;
        _contentBelowStrip = ContentZone.TopAnchor.ConstraintEqualTo(StripZone.BottomAnchor, gutter);
        _contentAtTop = ContentZone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, gutter);
        NSLayoutConstraint.ActivateConstraints(
        [
            StripZone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, gutter),
            StripZone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, gutter),
            StripZone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -gutter),
            _contentBelowStrip,
            ContentZone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, gutter),
            ContentZone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -gutter),
            ContentZone.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -gutter)
        ]);
    }

    public WinoZoneView StripZone { get; }
    public WinoZoneView ContentZone { get; }
    public CalendarWeekStripView WeekStrip { get; }

    /// <summary>Shows the week strip (Day view) or gives its height to the grid (Week and Month).</summary>
    public void SetStripVisible(bool visible)
    {
        StripZone.Hidden = !visible;
        _contentBelowStrip.Active = visible;
        _contentAtTop.Active = !visible;
    }

    /// <summary>The segment of a display type; Work week shows as Week on the phone.</summary>
    public static int SegmentFor(CalendarDisplayType type) => type switch
    {
        CalendarDisplayType.Day => 0,
        CalendarDisplayType.Month => 2,
        _ => 1
    };
}
