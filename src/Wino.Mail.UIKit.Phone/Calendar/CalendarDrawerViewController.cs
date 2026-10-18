using UIKit;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain;
using Wino.Mail.UIKit.Phone.Shell;
using Wino.Mail.UIKit.Views.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The Calendar content of the shell drawer (<see cref="ShellDrawer"/>; the iPad calendar sidebar's list): every
/// account's calendars with their colour toggles (<see cref="CalendarListView"/> over
/// <see cref="IAccountCalendarStateService"/>), which choose what the calendar shows, and the Settings row at the foot.
/// Toggles apply at once and keep the drawer open.
/// </summary>
internal sealed class CalendarDrawerViewController(IAccountCalendarStateService state, Action openSettings) : UIViewController
{
    private CalendarListView? _list;

    public override void LoadView()
    {
        NavigationItem.Title = Translator.IOS_Calendar_Calendars;
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        // The drawer's blur is the surface: the rows sit on it directly, without a zone.
        var root = new UIView { BackgroundColor = UIColor.Clear };
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true, BackgroundColor = UIColor.Clear };
        var list = new CalendarListView(state);
        _list = list;
        scroll.AddSubview(list);

        var column = WinoLayout.VStack(0, scroll, DrawerSettingsRow.Create(openSettings));
        column.Alignment = UIStackViewAlignment.Fill;
        column.TranslatesAutoresizingMaskIntoConstraints = false;
        root.AddSubview(column);
        var guide = root.SafeAreaLayoutGuide;
        var content = scroll.ContentLayoutGuide;
        var frame = scroll.FrameLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.TopAnchor.ConstraintEqualTo(guide.TopAnchor),
            column.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            column.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor),
            list.TopAnchor.ConstraintEqualTo(content.TopAnchor, 4),
            list.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -8),
            list.LeadingAnchor.ConstraintEqualTo(frame.LeadingAnchor, 8),
            list.TrailingAnchor.ConstraintEqualTo(frame.TrailingAnchor, -8)
        ]);
        View = root;
    }

    /// <summary>Calendar rows on screen (debug).</summary>
    public int CalendarRowCount => _list?.CalendarRowCount ?? 0;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _list?.Dispose();
        base.Dispose(disposing);
    }
}
