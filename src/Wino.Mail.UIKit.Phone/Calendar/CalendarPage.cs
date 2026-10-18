using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The iPhone Calendar tab's root view over the wallpaper (concept B of 18 Oct 2026): one content zone under the bar
/// whose body holds the timed surface or the agenda list. The date lives only in the bar title, which drops the date
/// panel (<see cref="CalendarDatePanelView"/>) over the zone; the grid's own column headers page with it.
/// </summary>
public sealed class CalendarPage : UIView
{
    public static readonly CalendarDisplayType[] DisplayTypes = [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.Month];

    public CalendarPage()
    {
        BackgroundColor = UIColor.Clear;
        ContentZone = new WinoZoneView();
        AddSubview(ContentZone);
        Body = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = UIColor.Clear };
        ContentZone.ContentView.ClipsToBounds = true;
        WinoLayout.Fill(Body, ContentZone.ContentView);

        var guide = SafeAreaLayoutGuide;
        var gutter = (nfloat)WinoStyle.ZoneGutter;
        NSLayoutConstraint.ActivateConstraints(
        [
            ContentZone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, gutter),
            ContentZone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, gutter),
            ContentZone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -gutter),
            ContentZone.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -gutter)
        ]);
    }

    public WinoZoneView ContentZone { get; }

    /// <summary>Hosts the timed surface and the agenda list.</summary>
    public UIView Body { get; }

    /// <summary>The segment of a display type; Work week shows as Week on the phone.</summary>
    public static int SegmentFor(CalendarDisplayType type) => type switch
    {
        CalendarDisplayType.Day => 0,
        CalendarDisplayType.Month => 2,
        _ => 1
    };
}
