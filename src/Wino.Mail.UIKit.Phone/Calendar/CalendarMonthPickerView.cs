using System.Globalization;
using CoreAnimation;
using CoreGraphics;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The iPhone calendar's month grid in the date picker sheet (replaces the shared <c>WinoMiniCalendarView</c> on the
/// phone): a header with previous and next month buttons around the month title, the weekday initials, and a paging
/// strip of three month pages so a horizontal swipe moves a month. The visible range shows as an accent band, today as
/// an accent circle; tapping the title returns to today's month. Each day is its own 40pt button for touch and
/// VoiceOver. The view is <see cref="PreferredHeight"/> tall; the host only sets its width.
/// </summary>
public sealed class CalendarMonthPickerView : UIView
{
    public const double PreferredHeight = HeaderHeight + HeaderGap + WeekdayHeight + Rows * CellHeight;

    private const double HeaderHeight = 44;
    private const double HeaderGap = 8;
    private const double WeekdayHeight = 20;
    private const double CellHeight = 40;
    private const double TodayCircle = 36;
    private const double BandHeight = 36;
    private const double BandRadius = 8;
    private const int Rows = WinoMiniCalendarGrid.Rows;
    private const int Columns = WinoMiniCalendarGrid.Columns;
    private const string MonthFormat = "MMMM yyyy";

    private readonly UIButton _previous;
    private readonly UIButton _next;
    private readonly UIButton _title;
    private readonly UILabel[] _weekdays = new UILabel[Columns];
    private readonly UIScrollView _pager;
    private readonly MonthPage[] _pages = new MonthPage[3];

    private DateOnly _displayMonth = WinoMiniCalendarGrid.MonthStart(DateOnly.FromDateTime(DateTime.Today));
    private IReadOnlySet<DateOnly> _selected = new HashSet<DateOnly>();
    private DayOfWeek _firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
    private int _pendingShift;
    private nfloat _laidOutWidth = -1;

    public event EventHandler<DateOnly>? DateClicked;
    public event EventHandler<DateOnly>? MonthChanged;

    public CalendarMonthPickerView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        BackgroundColor = UIColor.Clear;

        _previous = ChevronButton(WinoIconGlyph.ChevronLeft);
        _previous.PrimaryActionTriggered += (_, _) => Page(-1);
        _next = ChevronButton(WinoIconGlyph.ChevronRight);
        _next.PrimaryActionTriggered += (_, _) => Page(1);

        _title = new UIButton(UIButtonType.Custom) { TranslatesAutoresizingMaskIntoConstraints = false };
        _title.TitleLabel.Font = WinoStyle.Fixed(20, UIFontWeight.Bold);
        _title.TitleLabel.LineBreakMode = UILineBreakMode.TailTruncation;
        _title.SetTitleColor(WinoStyle.PrimaryText, UIControlState.Normal);
        _title.SetTitleColor(WinoStyle.SecondaryText, UIControlState.Highlighted);
        _title.AccessibilityTraits = UIAccessibilityTrait.Header | UIAccessibilityTrait.Button;
        _title.PrimaryActionTriggered += (_, _) => GoToToday();
        _title.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);

        var header = WinoLayout.HStack(0, _previous, _title, _next);
        header.Alignment = UIStackViewAlignment.Center;
        header.Distribution = UIStackViewDistribution.EqualCentering;

        var weekdays = WinoLayout.HStack(0);
        weekdays.Distribution = UIStackViewDistribution.FillEqually;
        for (int column = 0; column < Columns; column++)
        {
            var label = new UILabel
            {
                Font = WinoStyle.Fixed(11, UIFontWeight.Semibold),
                TextColor = WinoStyle.SecondaryText,
                TextAlignment = UITextAlignment.Center,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            _weekdays[column] = label;
            weekdays.AddArrangedSubview(label);
        }

        _pager = new UIScrollView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            PagingEnabled = true,
            ShowsHorizontalScrollIndicator = false,
            ShowsVerticalScrollIndicator = false,
            AlwaysBounceVertical = false,
            ScrollsToTop = false,
            ClipsToBounds = true
        };
        for (int index = 0; index < _pages.Length; index++)
        {
            var page = new MonthPage();
            page.DateClicked += (_, date) => DateClicked?.Invoke(this, date);
            _pages[index] = page;
            _pager.AddSubview(page);
        }
        _pager.DecelerationEnded += (_, _) => Settle();
        _pager.DraggingEnded += (_, args) => { if (!args.Decelerate) Settle(); };
        _pager.ScrollAnimationEnded += (_, _) => Settle();

        AddSubview(header);
        AddSubview(weekdays);
        AddSubview(_pager);
        NSLayoutConstraint.ActivateConstraints(
        [
            header.TopAnchor.ConstraintEqualTo(TopAnchor),
            header.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            header.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            header.HeightAnchor.ConstraintEqualTo((nfloat)HeaderHeight),
            weekdays.TopAnchor.ConstraintEqualTo(header.BottomAnchor, (nfloat)HeaderGap),
            weekdays.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            weekdays.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            weekdays.HeightAnchor.ConstraintEqualTo((nfloat)WeekdayHeight),
            _pager.TopAnchor.ConstraintEqualTo(weekdays.BottomAnchor),
            _pager.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _pager.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _pager.HeightAnchor.ConstraintEqualTo((nfloat)(Rows * CellHeight)),
            _pager.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);

        WinoTraits.OnAppearanceChanged(this, static view => view.Refresh());
        WinoStyle.AccentChanged += AccentChanged;
        Refresh();
    }

    /// <summary>Any day inside the month to show.</summary>
    public DateOnly DisplayMonth
    {
        get => _displayMonth;
        set
        {
            var first = WinoMiniCalendarGrid.MonthStart(value);
            if (_displayMonth == first) return;
            _displayMonth = first;
            Refresh();
        }
    }

    /// <summary>The visible range, drawn as the accent band.</summary>
    public IReadOnlySet<DateOnly> SelectedDates
    {
        get => _selected;
        set
        {
            _selected = value;
            Refresh();
        }
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => _firstDayOfWeek;
        set
        {
            if (_firstDayOfWeek == value) return;
            _firstDayOfWeek = value;
            Refresh();
        }
    }

    public override CGSize IntrinsicContentSize => new(NoIntrinsicMetric, (nfloat)PreferredHeight);

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var size = _pager.Bounds.Size;
        if (size.Width <= 0) return;
        for (int index = 0; index < _pages.Length; index++)
            _pages[index].Frame = new CGRect((double)size.Width * index, 0, (double)size.Width, (double)size.Height);
        _pager.ContentSize = new CGSize((double)size.Width * _pages.Length, (double)size.Height);
        // Keep the middle page in view after rotation or a size change, but never fight a swipe in progress.
        bool moving = _pager.Dragging || _pager.Decelerating || _pendingShift != 0;
        if (_laidOutWidth != size.Width)
        {
            // A size change cancels any paging animation, so its end callback may never come.
            _pendingShift = 0;
            RecenterPager();
        }
        else if (!moving) RecenterPager();
        _laidOutWidth = size.Width;
    }

    private static UIButton ChevronButton(WinoIconGlyph glyph)
    {
        var button = new UIButton(UIButtonType.Custom) { TranslatesAutoresizingMaskIntoConstraints = false };
        button.SetImage(WinoIcons.TemplateImage(glyph, 16), UIControlState.Normal);
        button.TintColor = WinoStyle.SecondaryText;
        WinoLayout.Size(button, 44, 44);
        return button;
    }

    private void AccentChanged(object? sender, EventArgs args) => Refresh();

    private void RecenterPager() => _pager.SetContentOffset(new CGPoint((double)_pager.Bounds.Width, 0), false);

    /// <summary>A chevron tap: slide to the neighbour page, then settle like a swipe.</summary>
    private void Page(int delta)
    {
        if (_pendingShift != 0 || _pager.Dragging || _pager.Decelerating) return;
        var width = _pager.Bounds.Width;
        if (width <= 0 || UIAccessibility.IsReduceMotionEnabled)
        {
            Shift(delta);
            return;
        }
        _pendingShift = delta;
        _pager.SetContentOffset(new CGPoint((double)width * (1 + delta), 0), true);
    }

    /// <summary>After a swipe or an animated page: move the month by the page left of or right of centre, then recentre.</summary>
    private void Settle()
    {
        _pendingShift = 0;
        var width = _pager.Bounds.Width;
        if (width <= 0) return;
        int page = (int)Math.Round((double)_pager.ContentOffset.X / (double)width);
        int delta = Math.Clamp(page, 0, _pages.Length - 1) - 1;
        if (delta == 0)
        {
            RecenterPager();
            return;
        }
        Shift(delta);
    }

    private void Shift(int delta)
    {
        _displayMonth = _displayMonth.AddMonths(delta);
        Refresh();
        RecenterPager();
        MonthChanged?.Invoke(this, _displayMonth);
    }

    private void GoToToday()
    {
        var month = WinoMiniCalendarGrid.MonthStart(DateOnly.FromDateTime(DateTime.Today));
        if (month == _displayMonth) return;
        _displayMonth = month;
        Refresh();
        RecenterPager();
        MonthChanged?.Invoke(this, _displayMonth);
    }

    private void Refresh()
    {
        var culture = CultureInfo.CurrentCulture;
        _title.SetTitle(_displayMonth.ToString(MonthFormat, culture), UIControlState.Normal);
        _title.AccessibilityLabel = _displayMonth.ToString(MonthFormat, culture);
        // No dedicated strings exist yet, so the paging buttons announce the month they open.
        _previous.AccessibilityLabel = _displayMonth.AddMonths(-1).ToString(MonthFormat, culture);
        _next.AccessibilityLabel = _displayMonth.AddMonths(1).ToString(MonthFormat, culture);

        var names = culture.DateTimeFormat.ShortestDayNames;
        for (int column = 0; column < Columns; column++)
            _weekdays[column].Text = names[WinoMiniCalendarGrid.DayNameIndex(column, _firstDayOfWeek)].ToUpper(culture);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var todayText = WinoStyle.ReadableText(WinoStyle.Accent, TraitCollection);
        for (int index = 0; index < _pages.Length; index++)
        {
            _pages[index].Update(_displayMonth.AddMonths(index - 1), _firstDayOfWeek, _selected, today, todayText, culture);
            // Only the shown month is reachable by VoiceOver; the chevrons page.
            _pages[index].AccessibilityElementsHidden = index != 1;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }

    /// <summary>One month: 42 day buttons over the range bands and the today circle, laid out in its own bounds.</summary>
    private sealed class MonthPage : UIView
    {
        private readonly UIButton[] _cells = new UIButton[WinoMiniCalendarGrid.CellCount];
        private readonly List<UIView> _bands = [];
        private readonly List<(int Row, int Start, int End, bool RoundLeading, bool RoundTrailing)> _runs = [];
        private readonly UIView _todayCircle;
        private DateOnly _gridStart;
        private int _todayIndex = -1;

        public event EventHandler<DateOnly>? DateClicked;

        public MonthPage()
        {
            BackgroundColor = UIColor.Clear;
            _todayCircle = new UIView { UserInteractionEnabled = false, Hidden = true };
            _todayCircle.Layer.CornerRadius = (nfloat)(TodayCircle / 2);
            AddSubview(_todayCircle);
            for (int index = 0; index < _cells.Length; index++)
            {
                var button = new UIButton(UIButtonType.Custom);
                int cell = index;
                button.PrimaryActionTriggered += (_, _) => DateClicked?.Invoke(this, _gridStart.AddDays(cell));
                _cells[index] = button;
                AddSubview(button);
            }
        }

        public void Update(DateOnly month, DayOfWeek firstDayOfWeek, IReadOnlySet<DateOnly> selected, DateOnly today, UIColor todayText, CultureInfo culture)
        {
            _gridStart = WinoMiniCalendarGrid.GridStart(month, firstDayOfWeek);
            _todayIndex = -1;
            _runs.Clear();
            int runStart = -1;
            bool runLeading = false;
            for (int index = 0; index < _cells.Length; index++)
            {
                var date = _gridStart.AddDays(index);
                int column = index % Columns;
                bool isSelected = selected.Contains(date);
                bool isToday = date == today;
                if (isToday) _todayIndex = index;

                if (isSelected)
                {
                    var (leading, trailing) = WinoMiniCalendarGrid.SelectionEnds(selected, date, column);
                    if (runStart < 0)
                    {
                        runStart = index;
                        runLeading = leading;
                    }
                    if (trailing || column == Columns - 1)
                    {
                        _runs.Add((index / Columns, runStart % Columns, column, runLeading, trailing));
                        runStart = -1;
                    }
                }

                var button = _cells[index];
                var color = isToday ? todayText : date.Month == month.Month ? WinoStyle.PrimaryText : WinoStyle.TertiaryText;
                button.SetTitle(date.Day.ToString(culture), UIControlState.Normal);
                button.SetTitleColor(color, UIControlState.Normal);
                button.TitleLabel.Font = WinoStyle.Fixed(16, isToday ? UIFontWeight.Semibold : UIFontWeight.Regular);
                button.AccessibilityLabel = date.ToString("D", culture);
                button.AccessibilityTraits = isSelected ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
            }

            _todayCircle.Hidden = _todayIndex < 0;
            _todayCircle.BackgroundColor = WinoStyle.Accent;

            while (_bands.Count < _runs.Count)
            {
                var band = new UIView { UserInteractionEnabled = false };
                band.Layer.CornerRadius = (nfloat)BandRadius;
                InsertSubviewBelow(band, _todayCircle);
                _bands.Add(band);
            }
            for (int index = 0; index < _bands.Count; index++)
            {
                var band = _bands[index];
                band.Hidden = index >= _runs.Count;
                if (band.Hidden) continue;
                var run = _runs[index];
                band.BackgroundColor = WinoStyle.Accent.ColorWithAlpha(0.16f);
                var corners = (CACornerMask)0;
                if (run.RoundLeading) corners |= CACornerMask.MinXMinYCorner | CACornerMask.MinXMaxYCorner;
                if (run.RoundTrailing) corners |= CACornerMask.MaxXMinYCorner | CACornerMask.MaxXMaxYCorner;
                band.Layer.MaskedCorners = corners;
            }
            SetNeedsLayout();
        }

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            double columnWidth = (double)Bounds.Width / Columns;
            for (int index = 0; index < _cells.Length; index++)
                _cells[index].Frame = CellRect(index, columnWidth);

            for (int index = 0; index < _runs.Count && index < _bands.Count; index++)
            {
                var run = _runs[index];
                double y = run.Row * CellHeight + (CellHeight - BandHeight) / 2;
                double x = run.Start * columnWidth;
                double right = (run.End + 1) * columnWidth;
                _bands[index].Frame = new CGRect(x, y, right - x, BandHeight);
            }

            if (_todayIndex >= 0)
            {
                var cell = CellRect(_todayIndex, columnWidth);
                _todayCircle.Frame = new CGRect((double)cell.GetMidX() - TodayCircle / 2, (double)cell.GetMidY() - TodayCircle / 2, TodayCircle, TodayCircle);
            }
        }

        private static CGRect CellRect(int index, double columnWidth)
            => new(index % Columns * columnWidth, index / Columns * CellHeight, columnWidth, CellHeight);
    }
}
