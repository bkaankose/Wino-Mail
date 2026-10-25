using System.Diagnostics;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// The Windows CalendarTitleBarContent with native AppKit controls, laid out like Apple Calendar:
/// previous/next as a momentary segmented control, the visible range text at 18pt, a Today button and
/// the WinoCalendarTypeSelectorControl as a select-one segmented control (Day / Week / Work week / Month).
/// AppKit owns pressing, keyboard focus, selection drawing and accessibility. Margin 4,0,7,8 like Windows.
/// The page wires the arrows to the calendar rail and the rest to the calendar shell commands.
/// The month rail runs vertically, so in the month view the arrows turn a quarter clockwise (left to
/// up, right to down) with the Windows CalendarTitleBarContent ease (260 ms, cubic-bezier 0.16, 1, 0.3, 1).
/// </summary>
internal sealed class CalendarToolbarView : NSView
{
    private static readonly CalendarDisplayType[] DisplayTypes =
        [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.WorkWeek, CalendarDisplayType.Month];

    /// <summary>A quarter turn clockwise takes the left arrow to up and the right arrow to down.</summary>
    private const double MonthArrowRotationDegrees = 90;
    private const double ArrowRotationMilliseconds = 260;
    private const double ArrowFrameSeconds = 1d / 120;

    private readonly NSTextField _rangeText;
    private readonly NSSegmentedControl _navigation;
    private readonly NSSegmentedControl _displayType;
    private readonly NSImage _previousArrow;
    private readonly NSImage _nextArrow;
    private readonly Stopwatch _arrowClock = new();
    private NSTimer? _arrowTimer;
    private double _arrowAngle;
    private double _arrowFromAngle;
    private double _arrowToAngle;
    private bool _hasSelectedType;
    private bool _hasPresentedArrows;

    public CalendarToolbarView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;

        _navigation = new NSSegmentedControl
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            TrackingMode = NSSegmentSwitchTracking.Momentary,
            SegmentCount = 2,
            ControlSize = NSControlSize.Large
        };
        _previousArrow = WinoIcons.Image(WinoIconGlyph.ArrowLeft, 12, null, Translator.Accessibility_PreviousCalendarPeriod);
        _nextArrow = WinoIcons.Image(WinoIconGlyph.ArrowRight, 12, null, Translator.Accessibility_NextCalendarPeriod);
        ApplyArrowImages();
        _navigation.SetToolTip(Translator.Accessibility_PreviousCalendarPeriod, 0);
        _navigation.SetToolTip(Translator.Accessibility_NextCalendarPeriod, 1);
        _navigation.SetWidth(40, 0);
        _navigation.SetWidth(40, 1);
        _navigation.Activated += (_, _) =>
        {
            if (_navigation.SelectedSegment == 0) PreviousRequested?.Invoke(this, EventArgs.Empty);
            else if (_navigation.SelectedSegment == 1) NextRequested?.Invoke(this, EventArgs.Empty);
        };

        _rangeText = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(18, NSFontWeight.Semibold));
        _rangeText.SetContentCompressionResistancePriority(260, NSLayoutConstraintOrientation.Horizontal);
        _rangeText.SetContentHuggingPriorityForOrientation(751, NSLayoutConstraintOrientation.Horizontal);

        var today = new NSButton
        {
            Title = Translator.Today,
            BezelStyle = NSBezelStyle.Push,
            ControlSize = NSControlSize.Large,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        today.Activated += (_, _) => TodayRequested?.Invoke(this, EventArgs.Empty);

        _displayType = new NSSegmentedControl
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            TrackingMode = NSSegmentSwitchTracking.SelectOne,
            SegmentCount = DisplayTypes.Length,
            ControlSize = NSControlSize.Large
        };
        string[] titles = [Translator.CalendarTypeSelector_Day, Translator.CalendarTypeSelector_Week, Translator.CalendarTypeSelector_WorkWeek, Translator.CalendarTypeSelector_Month];
        for (int index = 0; index < titles.Length; index++) _displayType.SetLabel(titles[index], index);
        WinoStyle.UseAccentSelection(_displayType);
        _displayType.Activated += (_, _) =>
        {
            var index = (int)_displayType.SelectedSegment;
            if (index >= 0 && index < DisplayTypes.Length) DisplayTypeRequested?.Invoke(this, DisplayTypes[index]);
        };

        _navigation.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        _displayType.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        // Today sits beside the period arrows, as in Calendar; the view switcher keeps the trailing edge.
        var row = WinoLayout.HStack(16, _navigation, today, _rangeText, WinoLayout.Spacer(), _displayType);
        row.SetCustomSpacing(8, _navigation);
        row.Alignment = NSLayoutAttribute.CenterY;
        row.EdgeInsets = new NSEdgeInsets(0, 4, 0, 7);
        WinoLayout.Fill(row, this);
        // A centred stack keeps its vertical insets only at low priority, so the row's height comes from
        // its tallest control; the calendar page adds the gap below it.
        HeightAnchor.ConstraintEqualTo(_displayType.HeightAnchor, 1, 4).Active = true;
    }

    public event EventHandler? PreviousRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? TodayRequested;
    public event EventHandler<CalendarDisplayType>? DisplayTypeRequested;

    public string RangeText
    {
        get => _rangeText.StringValue;
        set => _rangeText.StringValue = value ?? string.Empty;
    }

    /// <summary>The display type the page reports; other values (such as Year) leave no segment selected.</summary>
    public CalendarDisplayType SelectedType
    {
        set
        {
            _displayType.SelectedSegment = Array.IndexOf(DisplayTypes, value);
            // The first presentation takes the resting pose without a turn.
            bool animate = _hasSelectedType && _hasPresentedArrows && Window is not null &&
                           !NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion;
            _hasSelectedType = true;
            RotateArrows(value == CalendarDisplayType.Month ? MonthArrowRotationDegrees : 0, animate);
        }
    }

    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        if (Window is null)
        {
            // Leaving the window finishes a turn in flight.
            if (_arrowTimer is not null) RotateArrows(_arrowToAngle, animate: false);
            return;
        }
        _hasPresentedArrows = true;
    }

    private void RotateArrows(double angle, bool animate)
    {
        _arrowTimer?.Invalidate();
        _arrowTimer = null;
        _arrowClock.Stop();
        _arrowToAngle = angle;
        if (!animate || Math.Abs(angle - _arrowAngle) < 0.01)
        {
            _arrowAngle = angle;
            ApplyArrowImages();
            return;
        }

        _arrowFromAngle = _arrowAngle;
        _arrowClock.Restart();
        _arrowTimer = NSTimer.CreateRepeatingTimer(ArrowFrameSeconds, _ => ArrowFrame());
        NSRunLoop.Main.AddTimer(_arrowTimer, NSRunLoopMode.Common);
    }

    private void ArrowFrame()
    {
        double progress = _arrowClock.Elapsed.TotalMilliseconds / ArrowRotationMilliseconds;
        if (progress >= 1)
        {
            RotateArrows(_arrowToAngle, animate: false);
            return;
        }
        _arrowAngle = _arrowFromAngle + ((_arrowToAngle - _arrowFromAngle) * ArrowEasing.Evaluate(progress));
        ApplyArrowImages();
    }

    private void ApplyArrowImages()
    {
        _navigation.SetImage(Rotated(_previousArrow, _arrowAngle), 0);
        _navigation.SetImage(Rotated(_nextArrow, _arrowAngle), 1);
    }

    /// <summary>The arrow turned clockwise about its centre; it stays a template so the control tints it.</summary>
    private static NSImage Rotated(NSImage source, double clockwiseDegrees)
    {
        if (Math.Abs(clockwiseDegrees) < 0.01) return source;
        var size = source.Size;
        var image = NSImage.ImageWithSize(size, false, rect =>
        {
            var transform = new NSAffineTransform();
            transform.Translate((nfloat)(size.Width / 2), (nfloat)(size.Height / 2));
            // Unflipped drawing turns counter-clockwise for positive angles.
            transform.RotateByDegrees((nfloat)(-clockwiseDegrees));
            transform.Translate((nfloat)(-size.Width / 2), (nfloat)(-size.Height / 2));
            transform.Concat();
            source.Draw(rect, CGRect.Empty, NSCompositingOperation.SourceOver, 1);
            return true;
        });
        image.Template = source.Template;
        image.AccessibilityDescription = source.AccessibilityDescription;
        return image;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _arrowTimer?.Invalidate();
            _arrowTimer = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>The CSS cubic-bezier(0.16, 1, 0.3, 1) ease-out of the Windows arrow turn.</summary>
    private static class ArrowEasing
    {
        private const double X1 = 0.16, Y1 = 1, X2 = 0.3, Y2 = 1;

        public static double Evaluate(double progress)
        {
            progress = Math.Clamp(progress, 0, 1);
            return Sample(SolveCurveX(progress), Y1, Y2);
        }

        private static double Sample(double t, double p1, double p2)
        {
            double inverse = 1 - t;
            return (3 * inverse * inverse * t * p1) + (3 * inverse * t * t * p2) + (t * t * t);
        }

        private static double SampleDerivative(double t, double p1, double p2)
        {
            double inverse = 1 - t;
            return (3 * inverse * inverse * p1) + (6 * inverse * t * (p2 - p1)) + (3 * t * t * (1 - p2));
        }

        /// <summary>The curve parameter whose x is <paramref name="x"/>: Newton steps, then bisection.</summary>
        private static double SolveCurveX(double x)
        {
            double t = x;
            for (int step = 0; step < 8; step++)
            {
                double error = Sample(t, X1, X2) - x;
                if (Math.Abs(error) < 1e-6) return t;
                double slope = SampleDerivative(t, X1, X2);
                if (Math.Abs(slope) < 1e-6) break;
                t -= error / slope;
            }
            double low = 0, high = 1;
            t = x;
            while (high - low > 1e-6)
            {
                double value = Sample(t, X1, X2);
                if (Math.Abs(value - x) < 1e-6) return t;
                if (value < x) low = t; else high = t;
                t = (low + high) / 2;
            }
            return t;
        }
    }
}
