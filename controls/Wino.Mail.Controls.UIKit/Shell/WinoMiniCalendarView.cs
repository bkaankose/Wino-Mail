using System.Globalization;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Shell;

/// <summary>
/// The calendar pane's date picker (Windows ShellCalendarView; UIKit twin of the AppKit <c>WinoMiniCalendarView</c>)
/// over the shared <see cref="WinoMiniCalendarGrid"/>: a month card with the visible range highlighted in the accent
/// tint, today in an accent circle, previous and next month buttons and the collapse toggle. Drawn directly; taps are
/// hit-tested with the shared grid and VoiceOver sees the header buttons and one button per day.
/// </summary>
public sealed class WinoMiniCalendarView : UIView
{
    private const double Pad = WinoMiniCalendarGrid.Padding;
    private const double HeaderHeight = WinoMiniCalendarGrid.HeaderHeight;
    private const double NamesHeight = WinoMiniCalendarGrid.NamesHeight;
    private const double CellHeight = WinoMiniCalendarGrid.CellHeight;
    private const int Rows = WinoMiniCalendarGrid.Rows;

    private DateOnly _displayMonth = WinoMiniCalendarGrid.MonthStart(DateOnly.FromDateTime(DateTime.Today));
    private IReadOnlySet<DateOnly> _selected = new HashSet<DateOnly>();
    private DayOfWeek _firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
    private bool _collapsed;
    private WinoIconGlyph _toggleGlyph = WinoIconGlyph.PanelLeftContract;
    private readonly PressElement _previousElement;
    private readonly PressElement _nextElement;
    private readonly PressElement _toggleElement;
    private readonly PressElement[] _dayElements = new PressElement[WinoMiniCalendarGrid.CellCount];

    public event EventHandler<DateOnly>? DateClicked;
    public event EventHandler? ToggleClicked;

    public WinoMiniCalendarView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Opaque = false;
        BackgroundColor = UIColor.Clear;
        ContentMode = UIViewContentMode.Redraw;
        _previousElement = new PressElement(this, _ => DisplayMonth = _displayMonth.AddMonths(-1));
        _nextElement = new PressElement(this, _ => DisplayMonth = _displayMonth.AddMonths(1));
        _toggleElement = new PressElement(this, _ => ToggleClicked?.Invoke(this, EventArgs.Empty));
        for (int i = 0; i < _dayElements.Length; i++)
            _dayElements[i] = new PressElement(this, element => DateClicked?.Invoke(this, element.Date));
        AddGestureRecognizer(new UITapGestureRecognizer(Tapped));
        var swipeLeft = new UISwipeGestureRecognizer(() => DisplayMonth = _displayMonth.AddMonths(1)) { Direction = UISwipeGestureRecognizerDirection.Left };
        var swipeRight = new UISwipeGestureRecognizer(() => DisplayMonth = _displayMonth.AddMonths(-1)) { Direction = UISwipeGestureRecognizerDirection.Right };
        AddGestureRecognizer(swipeLeft);
        AddGestureRecognizer(swipeRight);
        RebuildAccessibility();
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
        WinoStyle.AccentChanged += AccentChanged;
    }

    private void AccentChanged(object? sender, EventArgs args) => SetNeedsDisplay();

    /// <summary>Any day inside the month to show.</summary>
    public DateOnly DisplayMonth
    {
        get => _displayMonth;
        set
        {
            var first = WinoMiniCalendarGrid.MonthStart(value);
            if (_displayMonth == first) return;
            _displayMonth = first;
            SetNeedsDisplay();
            RebuildAccessibility();
        }
    }

    public IReadOnlySet<DateOnly> SelectedDates
    {
        get => _selected;
        set
        {
            _selected = value;
            SetNeedsDisplay();
            foreach (var element in _dayElements) element.SetSelected(_selected.Contains(element.Date));
        }
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => _firstDayOfWeek;
        set
        {
            if (_firstDayOfWeek == value) return;
            _firstDayOfWeek = value;
            SetNeedsDisplay();
            RebuildAccessibility();
        }
    }

    /// <summary>Only the header row shows while collapsed.</summary>
    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            InvalidateIntrinsicContentSize();
            SetNeedsDisplay();
            RebuildAccessibility();
        }
    }

    public void SetToggle(WinoIconGlyph glyph, string? toolTip)
    {
        _toggleGlyph = glyph;
        _toggleElement.AccessibilityLabel = toolTip ?? string.Empty;
        SetNeedsDisplay();
    }

    public static double HeightFor(bool collapsed) => WinoMiniCalendarGrid.HeightFor(collapsed);

    public override CGSize IntrinsicContentSize => new(NoIntrinsicMetric, (nfloat)HeightFor(_collapsed));

    private CGRect HeaderRect(WinoMiniCalendarHeaderButton button)
    {
        var (x, y, width, height) = WinoMiniCalendarGrid.HeaderButtonBounds(button, Bounds.Width);
        return new CGRect(x, y, width, height);
    }

    private CGRect CellRect(int index)
    {
        var (x, y, width, height) = WinoMiniCalendarGrid.CellBounds(index, Bounds.Width);
        return new CGRect(x, y, width, height);
    }

    private DateOnly GridStart => WinoMiniCalendarGrid.GridStart(_displayMonth, _firstDayOfWeek);

    public override void Draw(CGRect rect)
    {
        var traits = TraitCollection;
        bool dark = WinoTraits.IsDark(traits);
        var text = UIColor.Label;
        var secondary = UIColor.SecondaryLabel;
        var tertiary = UIColor.TertiaryLabel;
        var accent = WinoStyle.Accent;
        nfloat radius = (nfloat)WinoStyle.GroupRadius;

        (dark ? WinoStyle.Hex(0xFFFFFF, 0.06) : WinoStyle.Hex(0xFFFFFF, 0.55)).SetFill();
        UIBezierPath.FromRoundedRect(Bounds, radius).Fill();
        (dark ? WinoStyle.Hex(0xFFFFFF, 0.07) : WinoStyle.Hex(0x000000, 0.06)).SetStroke();
        var border = UIBezierPath.FromRoundedRect(Bounds.Inset(0.5f, 0.5f), radius);
        border.LineWidth = 1;
        border.Stroke();

        var culture = CultureInfo.CurrentCulture;
        DrawText(_displayMonth.ToString("MMMM yyyy", culture), new CGRect(Pad, Pad, WinoMiniCalendarGrid.TitleWidth(Bounds.Width), HeaderHeight),
            WinoStyle.Fixed(13, UIFontWeight.Semibold), text, UITextAlignment.Left);
        WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.ChevronUp), HeaderRect(WinoMiniCalendarHeaderButton.Previous).Inset(2, 2), secondary, traits, false);
        WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.ChevronDown), HeaderRect(WinoMiniCalendarHeaderButton.Next).Inset(2, 2), secondary, traits, false);
        WinoIcons.Draw(WinoIcons.Glyph(_toggleGlyph), HeaderRect(WinoMiniCalendarHeaderButton.Toggle).Inset(1, 1), secondary, traits, false);
        if (_collapsed) return;

        double columnWidth = WinoMiniCalendarGrid.ColumnWidth(Bounds.Width);
        var names = culture.DateTimeFormat.ShortestDayNames;
        for (int column = 0; column < WinoMiniCalendarGrid.Columns; column++)
        {
            var day = WinoMiniCalendarGrid.DayNameIndex(column, _firstDayOfWeek);
            DrawText(names[day], new CGRect(Pad + column * columnWidth, WinoMiniCalendarGrid.NamesTop, columnWidth, NamesHeight),
                WinoStyle.Fixed(10), tertiary, UITextAlignment.Center);
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var date = GridStart;
        for (int index = 0; index < WinoMiniCalendarGrid.CellCount; index++, date = date.AddDays(1))
        {
            var cell = CellRect(index);
            int column = index % WinoMiniCalendarGrid.Columns;
            bool inMonth = date.Month == _displayMonth.Month;
            bool isToday = date == today;
            if (_selected.Contains(date))
            {
                var (left, right) = WinoMiniCalendarGrid.SelectionEnds(_selected, date, column);
                var band = new CGRect(cell.X, cell.Y + 1, cell.Width, cell.Height - 2);
                // Round only the ends of a contiguous run: extend the shape past a square side and clip to the cell.
                nfloat bandRadius = 6;
                var shape = new CGRect(band.X - (left ? 0 : bandRadius), band.Y, band.Width + (left ? 0 : bandRadius) + (right ? 0 : bandRadius), band.Height);
                var context = UIGraphics.GetCurrentContext();
                context.SaveState();
                context.ClipToRect(band);
                accent.ColorWithAlpha(0.16f).SetFill();
                UIBezierPath.FromRoundedRect(shape, bandRadius).Fill();
                context.RestoreState();
            }
            if (isToday)
            {
                accent.SetFill();
                UIBezierPath.FromOval(new CGRect(cell.GetMidX() - 11, cell.GetMidY() - 11, 22, 22)).Fill();
            }
            var color = isToday ? UIColor.White : inMonth ? text : tertiary;
            var font = WinoStyle.Fixed(12, isToday ? UIFontWeight.Semibold : UIFontWeight.Regular);
            DrawText(date.Day.ToString(culture), cell, font, color, UITextAlignment.Center);
        }
    }

    private static void DrawText(string value, CGRect rect, UIFont font, UIColor color, UITextAlignment alignment)
    {
        var paragraph = new NSMutableParagraphStyle { Alignment = alignment, LineBreakMode = UILineBreakMode.TailTruncation };
        var attributed = new NSAttributedString(value, new UIStringAttributes { Font = font, ForegroundColor = color, ParagraphStyle = paragraph });
        var height = font.LineHeight;
        attributed.DrawString(new CGRect(rect.X, rect.Y + (rect.Height - height) / 2, rect.Width, height));
    }

    private void Tapped(UITapGestureRecognizer recognizer)
    {
        var point = recognizer.LocationInView(this);
        switch (WinoMiniCalendarGrid.HitTestHeader(point.X, point.Y, Bounds.Width))
        {
            case WinoMiniCalendarHeaderButton.Toggle: ToggleClicked?.Invoke(this, EventArgs.Empty); return;
            case WinoMiniCalendarHeaderButton.Previous: DisplayMonth = _displayMonth.AddMonths(-1); return;
            case WinoMiniCalendarHeaderButton.Next: DisplayMonth = _displayMonth.AddMonths(1); return;
        }
        if (_collapsed) return;
        int index = WinoMiniCalendarGrid.HitTest(point.X, point.Y, Bounds.Width);
        if (index >= 0) DateClicked?.Invoke(this, GridStart.AddDays(index));
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        UpdateAccessibilityFrames();
    }

    /// <summary>Relabels the header buttons and day elements; runs only when the shown month or grid shape changes.</summary>
    private void RebuildAccessibility()
    {
        var culture = CultureInfo.CurrentCulture;
        AccessibilityLabel = _displayMonth.ToString("MMMM yyyy", culture);
        // No dedicated strings exist yet, so the paging buttons announce the month they open.
        _previousElement.AccessibilityLabel = _displayMonth.AddMonths(-1).ToString("MMMM yyyy", culture);
        _nextElement.AccessibilityLabel = _displayMonth.AddMonths(1).ToString("MMMM yyyy", culture);
        var children = new List<NSObject>(3 + _dayElements.Length) { _previousElement, _nextElement, _toggleElement };
        if (!_collapsed)
        {
            var date = GridStart;
            foreach (var element in _dayElements)
            {
                element.Date = date;
                element.AccessibilityLabel = date.ToString("D", culture);
                element.SetSelected(_selected.Contains(date));
                children.Add(element);
                date = date.AddDays(1);
            }
        }
        // The binding exposes accessibilityElements only through the container protocol; set it by key.
        SetValueForKey(NSArray.FromNSObjects(children.ToArray()), new NSString("accessibilityElements"));
        UpdateAccessibilityFrames();
    }

    private void UpdateAccessibilityFrames()
    {
        _previousElement.AccessibilityFrameInContainerSpace = HeaderRect(WinoMiniCalendarHeaderButton.Previous).Inset(-12, -12);
        _nextElement.AccessibilityFrameInContainerSpace = HeaderRect(WinoMiniCalendarHeaderButton.Next).Inset(-12, -12);
        _toggleElement.AccessibilityFrameInContainerSpace = HeaderRect(WinoMiniCalendarHeaderButton.Toggle).Inset(-12, -12);
        for (int i = 0; i < _dayElements.Length; i++) _dayElements[i].AccessibilityFrameInContainerSpace = CellRect(i);
    }

    /// <summary>A pressable accessibility child standing in for a drawn hit region.</summary>
    private sealed class PressElement : UIAccessibilityElement
    {
        private readonly Action<PressElement> _press;

        public PressElement(UIView container, Action<PressElement> press) : base(container)
        {
            _press = press;
            IsAccessibilityElement = true;
            AccessibilityTraits = (ulong)UIAccessibilityTrait.Button;
        }

        public DateOnly Date { get; set; }

        public void SetSelected(bool selected)
            => AccessibilityTraits = (ulong)(selected ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button);

        [Export("accessibilityActivate")]
        public bool Activate() { _press(this); return true; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }
}
