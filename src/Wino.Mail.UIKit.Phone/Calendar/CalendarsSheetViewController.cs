using UIKit;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain;
using Wino.Mail.UIKit.Views.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The iPhone calendars filter sheet: every account's calendars with their colour toggles (<see cref="CalendarListView"/>
/// over <see cref="IAccountCalendarStateService"/>). Toggles apply at once; Done closes the sheet.
/// </summary>
public sealed class CalendarsSheetViewController(IAccountCalendarStateService state, Action done) : UIViewController
{
    private CalendarListView? _list;

    public override void LoadView()
    {
        Title = Translator.IOS_Calendar_Calendars;
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => done());
        var root = new UIView { BackgroundColor = UIColor.SystemGroupedBackground };
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true };
        var list = new CalendarListView(state);
        _list = list;
        var card = new WinoSurfaceView { Fill = WinoStyle.GroupFill, CornerRadius = 10 };
        WinoLayout.Fill(list, card, 4, 0, 4, 0);
        scroll.AddSubview(card);
        root.AddSubview(scroll);
        WinoLayout.Fill(scroll, root);
        var content = scroll.ContentLayoutGuide;
        var frame = scroll.FrameLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            card.TopAnchor.ConstraintEqualTo(content.TopAnchor, 12),
            card.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -12),
            card.LeadingAnchor.ConstraintEqualTo(frame.LeadingAnchor, 16),
            card.TrailingAnchor.ConstraintEqualTo(frame.TrailingAnchor, -16)
        ]);
        View = root;
    }

    /// <summary>Calendar rows on screen (debug).</summary>
    public int CalendarRowCount => _list?.CalendarRowCount ?? 0;
}
