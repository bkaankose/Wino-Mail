using System.Globalization;
using CoreAnimation;
using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>What the date selector asks of the calendar page.</summary>
internal interface ICalendarPickerHost
{
    CalendarDisplayType DisplayType { get; }
    VisibleDateRange? VisibleRange { get; }
    DayOfWeek FirstDayOfWeek { get; }
    void PickToday();
    void PickDate(DateOnly date);
    void ClosePicker();
    void PickerVisibilityChanged(bool open);
}

/// <summary>
/// The calendar's date selector (calendar refresh of 14 Nov 2026): a sheet attached to the top edge that reads as the bar
/// growing downward, over a scrim. The month name with the paging chevrons, the month grid
/// (<see cref="CalendarMonthPickerView"/>) with the visible range, then Today and the week number, and a grabber. Picking
/// a day or Today goes there and closes the sheet; paging months keeps it open; a tap on the scrim or a swipe up closes
/// it. The view switch lives in the bar's view menu. On iPad the sheet is a 420pt card under the title.
/// </summary>
internal sealed class CalendarDatePanelView : UIView
{
    private const double BottomRadius = 30;
    private const double PadRadius = 24;
    private const double PadWidth = 420;

    private readonly ICalendarPickerHost _host;
    private readonly UIView _scrim = new()
    {
        TranslatesAutoresizingMaskIntoConstraints = false,
        BackgroundColor = WinoStyle.Dynamic(UIColor.Black.ColorWithAlpha(0.14f), UIColor.Black.ColorWithAlpha(0.40f))
    };
    private readonly UIView _sheet = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIVisualEffectView _material = new(UIBlurEffect.FromStyle(UIBlurEffectStyle.SystemThickMaterial)) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UILabel _monthTitle = new() { TranslatesAutoresizingMaskIntoConstraints = false, Font = WinoStyle.Fixed(22, UIFontWeight.Bold), TextColor = WinoStyle.PrimaryText };
    private readonly UIButton _previous;
    private readonly UIButton _next;
    private readonly UIButton _today;
    private readonly UILabel _week = new() { TranslatesAutoresizingMaskIntoConstraints = false, Font = WinoStyle.Fixed(13), TextColor = WinoStyle.SecondaryText };
    private readonly CalendarMonthPickerView _month = new();

    public CalendarDatePanelView(ICalendarPickerHost host)
    {
        _host = host;
        TranslatesAutoresizingMaskIntoConstraints = false;
        Hidden = true;
        _scrim.AddGestureRecognizer(new UITapGestureRecognizer(() => host.ClosePicker()));
        _scrim.IsAccessibilityElement = true;
        _scrim.AccessibilityLabel = Translator.Buttons_Close;
        _scrim.AccessibilityTraits = UIAccessibilityTrait.Button;
        WinoLayout.Fill(_scrim, this);

        bool pad = PhoneShellViewController.IsPad;
        double radius = pad ? PadRadius : BottomRadius;
        _sheet.Layer.ShadowColor = UIColor.Black.CGColor;
        _sheet.Layer.ShadowOpacity = 0.2f;
        _sheet.Layer.ShadowRadius = 17;
        _sheet.Layer.ShadowOffset = new CGSize(0, 14);
        _sheet.AccessibilityViewIsModal = true;
        _material.Layer.CornerRadius = (nfloat)radius;
        _material.Layer.CornerCurve = CACornerCurve.Continuous;
        _material.Layer.MaskedCorners = pad
            ? CACornerMask.MinXMinYCorner | CACornerMask.MaxXMinYCorner | CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner
            : CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner;
        _material.ClipsToBounds = true;
        WinoLayout.Fill(_material, _sheet);

        _monthTitle.AccessibilityTraits = UIAccessibilityTrait.Header;
        _monthTitle.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        _previous = Chevron(WinoIconGlyph.ChevronLeft, () => _month.Page(-1));
        _next = Chevron(WinoIconGlyph.ChevronRight, () => _month.Page(1));
        var header = WinoLayout.HStack(0, _monthTitle, WinoLayout.Spacer(), _previous, _next);
        header.Alignment = UIStackViewAlignment.Center;

        _month.DateClicked += (_, date) => host.PickDate(date);
        _month.MonthChanged += (_, _) => UpdateMonthTitle();

        var todayConfiguration = UIButtonConfiguration.FilledButtonConfiguration;
        todayConfiguration.AttributedTitle = new Foundation.NSAttributedString(Translator.Today, new UIStringAttributes { Font = WinoStyle.Fixed(15, UIFontWeight.Semibold) });
        todayConfiguration.BaseForegroundColor = WinoStyle.Accent;
        todayConfiguration.BaseBackgroundColor = WinoStyle.Dynamic(WinoStyle.Accent.ColorWithAlpha(0.14f), WinoStyle.Accent.ColorWithAlpha(0.22f));
        todayConfiguration.CornerStyle = UIButtonConfigurationCornerStyle.Capsule;
        todayConfiguration.ContentInsets = new NSDirectionalEdgeInsets(0, 16, 0, 16);
        _today = new UIButton { Configuration = todayConfiguration, TranslatesAutoresizingMaskIntoConstraints = false };
        _today.HeightAnchor.ConstraintEqualTo(36).Active = true;
        _today.PrimaryActionTriggered += (_, _) => host.PickToday();
        var footer = WinoLayout.HStack(0, _today, WinoLayout.Spacer(), _week);
        footer.Alignment = UIStackViewAlignment.Center;

        var grabber = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = WinoStyle.TertiaryText, UserInteractionEnabled = false };
        grabber.Layer.CornerRadius = 2;
        WinoLayout.Size(grabber, 36, 4);

        var content = _material.ContentView;
        content.AddSubview(header);
        content.AddSubview(_month);
        content.AddSubview(footer);
        content.AddSubview(grabber);
        AddSubview(_sheet);

        var guide = SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            pad ? header.TopAnchor.ConstraintEqualTo(_sheet.TopAnchor, 12) : header.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 8),
            header.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 20),
            header.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -8),
            header.HeightAnchor.ConstraintEqualTo(44),
            _month.TopAnchor.ConstraintEqualTo(header.BottomAnchor, 4),
            _month.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 12),
            _month.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -12),
            footer.TopAnchor.ConstraintEqualTo(_month.BottomAnchor, 12),
            footer.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 20),
            footer.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -20),
            grabber.TopAnchor.ConstraintEqualTo(footer.BottomAnchor, 14),
            grabber.CenterXAnchor.ConstraintEqualTo(content.CenterXAnchor),
            grabber.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -6)
        ]);
        if (pad)
        {
            // The iPad bar is far wider than a month needs: a card under the title.
            NSLayoutConstraint.ActivateConstraints(
            [
                _sheet.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 4),
                _sheet.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, 8)
            ]);
            var width = _sheet.WidthAnchor.ConstraintEqualTo((nfloat)PadWidth);
            width.Priority = 750;
            width.Active = true;
            var fit = _sheet.TrailingAnchor.ConstraintLessThanOrEqualTo(guide.TrailingAnchor, -8);
            fit.Active = true;
        }
        else
        {
            NSLayoutConstraint.ActivateConstraints(
            [
                _sheet.TopAnchor.ConstraintEqualTo(TopAnchor),
                _sheet.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
                _sheet.TrailingAnchor.ConstraintEqualTo(TrailingAnchor)
            ]);
        }

        var swipe = new UISwipeGestureRecognizer(() => host.ClosePicker()) { Direction = UISwipeGestureRecognizerDirection.Up };
        _sheet.AddGestureRecognizer(swipe);
        Refresh();
    }

    public bool IsOpen => !Hidden;

    private static UIButton Chevron(WinoIconGlyph glyph, Action action)
    {
        var button = new UIButton(UIButtonType.Custom) { TranslatesAutoresizingMaskIntoConstraints = false };
        button.SetImage(WinoIcons.TemplateImage(glyph, 18), UIControlState.Normal);
        button.TintColor = WinoStyle.Accent;
        WinoLayout.Size(button, 44, 44);
        button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }

    /// <summary>Extends the sheet from under the bar (a fade under Reduce Motion).</summary>
    public void Open()
    {
        if (!Hidden) return;
        Refresh();
        Hidden = false;
        Superview?.BringSubviewToFront(this);
        LayoutIfNeeded();
        _scrim.Alpha = 0;
        _sheet.Alpha = 0;
        if (!UIAccessibility.IsReduceMotionEnabled) _sheet.Transform = CGAffineTransform.MakeTranslation(0, -24);
        UIView.AnimateNotify(0.32, 0, 0.86f, 0, UIViewAnimationOptions.AllowUserInteraction, () =>
        {
            _scrim.Alpha = 1;
            _sheet.Alpha = 1;
            _sheet.Transform = CGAffineTransform.MakeIdentity();
        }, null);
        _host.PickerVisibilityChanged(true);
        UIAccessibility.PostNotification(UIAccessibilityPostNotification.ScreenChanged, _sheet);
    }

    public void Close()
    {
        if (Hidden) return;
        _host.PickerVisibilityChanged(false);
        UIView.Animate(0.18, 0, UIViewAnimationOptions.BeginFromCurrentState, () =>
        {
            _scrim.Alpha = 0;
            _sheet.Alpha = 0;
            if (!UIAccessibility.IsReduceMotionEnabled) _sheet.Transform = CGAffineTransform.MakeTranslation(0, -16);
        }, () =>
        {
            Hidden = true;
            _sheet.Transform = CGAffineTransform.MakeIdentity();
            UIAccessibility.PostNotification(UIAccessibilityPostNotification.ScreenChanged, null);
        });
    }

    public override bool AccessibilityPerformEscape()
    {
        _host.ClosePicker();
        return true;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _sheet.Layer.ShadowPath = UIBezierPath.FromRoundedRect(_sheet.Bounds, (nfloat)(PhoneShellViewController.IsPad ? PadRadius : BottomRadius)).CGPath;
    }

    /// <summary>Reads the page state again (the range or the period changed).</summary>
    public void Refresh()
    {
        _month.FirstDayOfWeek = _host.FirstDayOfWeek;
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_host.VisibleRange is { } range)
        {
            var dates = range.Dates.ToList();
            switch (_host.DisplayType)
            {
                case CalendarDisplayType.Day:
                    _month.SelectedDates = new HashSet<DateOnly>();
                    _month.CircleDate = range.AnchorDate;
                    break;
                case CalendarDisplayType.Month:
                    _month.SelectedDates = new HashSet<DateOnly>();
                    _month.CircleDate = null;
                    break;
                default:
                    _month.SelectedDates = new HashSet<DateOnly>(dates);
                    _month.CircleDate = dates.Contains(today) ? today : null;
                    break;
            }
            _month.DisplayMonth = range.AnchorDate;
            var first = dates.Count > 0 ? dates[0] : range.AnchorDate;
            _week.Text = string.Format(CultureInfo.CurrentCulture, Translator.IOS_Calendar_WeekNumber,
                ISOWeek.GetWeekOfYear(first.ToDateTime(TimeOnly.MinValue)));
        }
        UpdateMonthTitle();
    }

    private void UpdateMonthTitle()
    {
        var culture = CultureInfo.CurrentCulture;
        var month = _month.DisplayMonth;
        _monthTitle.Text = month.ToDateTime(TimeOnly.MinValue).ToString("Y", culture);
        // The chevrons announce the month they open.
        _previous.AccessibilityLabel = month.AddMonths(-1).ToDateTime(TimeOnly.MinValue).ToString("Y", culture);
        _next.AccessibilityLabel = month.AddMonths(1).ToDateTime(TimeOnly.MinValue).ToString("Y", culture);
    }

#if DEBUG
    public string Describe()
        => $"open={IsOpen} month='{_monthTitle.Text}' week='{_week.Text}' sheet={_sheet.Frame} circle={_month.CircleDate} band={_month.SelectedDates.Count}";
#endif
}
