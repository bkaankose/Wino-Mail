using System.Globalization;
using CoreGraphics;
using UIKit;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The week strip under the bar in Day and in the list (calendar refresh of 14 Nov 2026; twin of the Android
/// WinoCalendarWeekStripView): seven equal cells with the weekday letter over the day number in a 36pt circle. The
/// selected day is a filled circle (accent on today, the label colour otherwise), today unselected has accent digits and
/// weekends secondary digits. A tap raises <see cref="DateTapped"/>; the page pages the strip by a week with a
/// horizontal drag (<see cref="CalendarPagingInteraction"/>).
/// </summary>
internal sealed class CalendarWeekStripView : UIView
{
    public const double Height = 64;
    private const double SidePadding = 8;
    private const double Circle = 36;

    private readonly DayCell[] _cells = new DayCell[7];
    private readonly UIView _rule = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UISelectionFeedbackGenerator _feedback = new();
    private DateOnly _selected = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private DayOfWeek _firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    public event EventHandler<DateOnly>? DateTapped;

    public CalendarWeekStripView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        BackgroundColor = UIColor.Clear;
        ClipsToBounds = true;
        var row = new UIStackView { TranslatesAutoresizingMaskIntoConstraints = false, Axis = UILayoutConstraintAxis.Horizontal, Distribution = UIStackViewDistribution.FillEqually };
        for (int index = 0; index < _cells.Length; index++)
        {
            var cell = new DayCell();
            int column = index;
            cell.PrimaryActionTriggered += (_, _) => DateTapped?.Invoke(this, WeekStart.AddDays(column));
            _cells[index] = cell;
            row.AddArrangedSubview(cell);
        }
        AddSubview(row);
        AddSubview(_rule);
        _rule.BackgroundColor = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.09), WinoStyle.Hex(0xFFFFFF, 0.11));
        NSLayoutConstraint.ActivateConstraints(
        [
            row.TopAnchor.ConstraintEqualTo(TopAnchor),
            row.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, (nfloat)SidePadding),
            row.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -(nfloat)SidePadding),
            _rule.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _rule.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _rule.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            _rule.HeightAnchor.ConstraintEqualTo(1 / UIScreen.MainScreen.Scale)
        ]);
        WinoTraits.OnAppearanceChanged(this, static view => view.Refresh());
        WinoStyle.AccentWatchers.Add(this, static view => view.Refresh());
        Refresh();
    }

    /// <summary>The first day of the shown week.</summary>
    public DateOnly WeekStart
    {
        get
        {
            int offset = ((int)_selected.DayOfWeek - (int)_firstDayOfWeek + 7) % 7;
            return _selected.AddDays(-offset);
        }
    }

    /// <summary>Sets the selected day (the strip shows its week), today and the first weekday; a new selection ticks.</summary>
    public void Update(DateOnly selected, DateOnly today, DayOfWeek firstDayOfWeek, bool feedback)
    {
        if (selected == _selected && today == _today && firstDayOfWeek == _firstDayOfWeek) return;
        bool changed = selected != _selected;
        _selected = selected;
        _today = today;
        _firstDayOfWeek = firstDayOfWeek;
        Refresh();
        if (changed && feedback) _feedback.SelectionChanged();
    }

    public DateOnly SelectedDate => _selected;

    private void Refresh()
    {
        var culture = CultureInfo.CurrentCulture;
        var start = WeekStart;
        var traits = TraitCollection;
        for (int index = 0; index < _cells.Length; index++)
        {
            var date = start.AddDays(index);
            _cells[index].Configure(date, date == _selected, date == _today, culture, traits);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentWatchers.Remove(this);
        base.Dispose(disposing);
    }

    /// <summary>One day: the weekday letter over the number in its circle.</summary>
    private sealed class DayCell : UIControl
    {
        private readonly UILabel _weekday = new() { Font = WinoStyle.Fixed(11, UIFontWeight.Semibold), TextColor = WinoStyle.SecondaryText, TextAlignment = UITextAlignment.Center };
        private readonly UILabel _number = new() { TextAlignment = UITextAlignment.Center };
        private readonly UIView _circle = new() { UserInteractionEnabled = false };

        public DayCell()
        {
            _circle.Layer.CornerRadius = (nfloat)(Circle / 2);
            AddSubview(_weekday);
            AddSubview(_circle);
            AddSubview(_number);
            IsAccessibilityElement = true;
        }

        public void Configure(DateOnly date, bool selected, bool today, CultureInfo culture, UITraitCollection traits)
        {
            var name = culture.DateTimeFormat.ShortestDayNames[(int)date.DayOfWeek];
            _weekday.Text = (name.Length > 0 ? name[..1] : name).ToUpper(culture);
            _number.Text = date.Day.ToString(culture);
            bool weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            _circle.Hidden = !selected;
            _circle.BackgroundColor = today ? WinoStyle.Accent : WinoStyle.PrimaryText;
            _number.Font = WinoStyle.Fixed(18, selected || today ? UIFontWeight.Semibold : UIFontWeight.Medium);
            _number.TextColor = selected
                ? (today ? WinoStyle.ReadableText(WinoStyle.Accent, traits) : WinoStyle.ZoneFill)
                : today ? WinoStyle.Accent : weekend ? WinoStyle.SecondaryText : WinoStyle.PrimaryText;
            AccessibilityLabel = date.ToDateTime(TimeOnly.MinValue).ToString("D", culture);
            AccessibilityTraits = selected ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
        }

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            var bounds = Bounds;
            _weekday.Frame = new CGRect(0, 6, bounds.Width, 14);
            var circle = new CGRect(bounds.GetMidX() - Circle / 2, 22, Circle, Circle);
            _circle.Frame = circle;
            _number.Frame = circle;
        }
    }
}
