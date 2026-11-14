using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The Calendar tab's root view (calendar refresh of 14 Nov 2026): one full-bleed surface under the bar and the floating
/// tab bar, no zone. From the bar's bottom down: the week strip (Day, and the list in Day and Week), then the body. The
/// body holds the surface slot (the timed surface or the month grid) and, in the compact month, the selected day's list
/// under a 306pt grid; the agenda list covers the body when "Show as list" is on.
/// </summary>
public sealed class CalendarPage : UIView
{
    /// <summary>The periods of the view menu. The iPad adds Work week, which its wider grid has room for.</summary>
    public static readonly CalendarDisplayType[] DisplayTypes = PhoneShellViewController.IsPad
        ? [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.WorkWeek, CalendarDisplayType.Month]
        : [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.Month];

    public static string TitleOf(CalendarDisplayType type) => type switch
    {
        CalendarDisplayType.Day => Translator.CalendarTypeSelector_Day,
        CalendarDisplayType.WorkWeek => Translator.CalendarTypeSelector_WorkWeek,
        CalendarDisplayType.Month => Translator.CalendarTypeSelector_Month,
        _ => Translator.CalendarTypeSelector_Week
    };

    public static WinoIconGlyph GlyphOf(CalendarDisplayType type) => type switch
    {
        CalendarDisplayType.Day => WinoIconGlyph.CalendarToday,
        CalendarDisplayType.WorkWeek => WinoIconGlyph.CalendarWorkWeek,
        CalendarDisplayType.Month => WinoIconGlyph.Calendar,
        _ => WinoIconGlyph.CalendarWeek
    };

    private readonly NSLayoutConstraint _stripHeight;
    private readonly NSLayoutConstraint _slotFill;
    private readonly NSLayoutConstraint _slotCompact;

    internal CalendarPage()
    {
        BackgroundColor = UIColor.Clear;
        Background = new WinoPageSurfaceView();
        WinoLayout.Fill(Background, this);

        Strip = new CalendarWeekStripView();
        Body = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = UIColor.Clear, ClipsToBounds = true };
        SurfaceSlot = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = UIColor.Clear };
        MonthListSlot = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = UIColor.Clear, Hidden = true };
        AddSubview(Body);
        AddSubview(Strip);
        Body.AddSubview(SurfaceSlot);
        Body.AddSubview(MonthListSlot);

        var guide = SafeAreaLayoutGuide;
        _stripHeight = Strip.HeightAnchor.ConstraintEqualTo(0);
        _slotFill = SurfaceSlot.BottomAnchor.ConstraintEqualTo(Body.BottomAnchor);
        _slotCompact = SurfaceSlot.HeightAnchor.ConstraintEqualTo((nfloat)WinoCalendarSurfaceView.CompactMonthHeight);
        NSLayoutConstraint.ActivateConstraints(
        [
            Strip.TopAnchor.ConstraintEqualTo(guide.TopAnchor),
            Strip.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor),
            Strip.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor),
            _stripHeight,
            Body.TopAnchor.ConstraintEqualTo(Strip.BottomAnchor),
            Body.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor),
            Body.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor),
            Body.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            SurfaceSlot.TopAnchor.ConstraintEqualTo(Body.TopAnchor),
            SurfaceSlot.LeadingAnchor.ConstraintEqualTo(Body.LeadingAnchor),
            SurfaceSlot.TrailingAnchor.ConstraintEqualTo(Body.TrailingAnchor),
            _slotFill,
            MonthListSlot.TopAnchor.ConstraintEqualTo(SurfaceSlot.BottomAnchor),
            MonthListSlot.LeadingAnchor.ConstraintEqualTo(Body.LeadingAnchor),
            MonthListSlot.TrailingAnchor.ConstraintEqualTo(Body.TrailingAnchor),
            MonthListSlot.BottomAnchor.ConstraintEqualTo(Body.BottomAnchor)
        ]);
        Strip.Hidden = true;
    }

    public WinoPageSurfaceView Background { get; }

    internal CalendarWeekStripView Strip { get; }

    /// <summary>Hosts the surface slot, the month day list and the agenda list.</summary>
    public UIView Body { get; }

    /// <summary>Hosts the timed surface or the month grid, and the readiness view.</summary>
    public UIView SurfaceSlot { get; }

    /// <summary>The compact month's day list under the grid.</summary>
    public UIView MonthListSlot { get; }

    public bool IsStripVisible => !Strip.Hidden;

    public bool IsCompactMonth => _slotCompact.Active;

    /// <summary>Shows or hides the week strip; it slides down from under the bar.</summary>
    internal void SetStripVisible(bool visible, bool animated)
    {
        if (visible == !Strip.Hidden) return;
        if (visible) Strip.Hidden = false;
        _stripHeight.Constant = visible ? (nfloat)CalendarWeekStripView.Height : 0;
        if (!animated || Window is null)
        {
            Strip.Hidden = !visible;
            return;
        }
        UIView.Animate(UIAccessibility.IsReduceMotionEnabled ? 0.15 : 0.25, 0, UIViewAnimationOptions.CurveEaseOut | UIViewAnimationOptions.BeginFromCurrentState, LayoutIfNeeded, () =>
        {
            if (_stripHeight.Constant == 0) Strip.Hidden = true;
        });
    }

    /// <summary>The compact month: a 306pt grid with the selected day's list under it; otherwise the slot fills the body.</summary>
    internal void SetCompactMonth(bool compact)
    {
        if (compact == _slotCompact.Active) return;
        if (compact)
        {
            _slotFill.Active = false;
            _slotCompact.Active = true;
        }
        else
        {
            _slotCompact.Active = false;
            _slotFill.Active = true;
        }
        MonthListSlot.Hidden = !compact;
    }
}
