using System.Globalization;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Shell;

/// <summary>
/// The calendar pane's date picker (Windows ShellCalendarView): a month card with the visible
/// range highlighted in the accent tint, today in an accent circle, previous/next month chevrons
/// and the collapse toggle. Drawn directly; cells are hit-tested in <see cref="MouseDown"/>.
/// The keyboard moves a focused day (arrows, Page Up/Down, Home) and Return/Space selects it;
/// VoiceOver sees the header buttons and one pressable element per day.
/// </summary>
public sealed class WinoMiniCalendarView : NSView
{
    private const double Pad = 10;
    private const double HeaderHeight = 22;
    private const double NamesHeight = 16;
    private const double CellHeight = 24;
    private const int Rows = 6;
    private const ushort KeyLeft = 123, KeyRight = 124, KeyArrowDown = 125, KeyArrowUp = 126;
    private const ushort KeyPageUp = 116, KeyPageDown = 121, KeyHome = 115;
    private const ushort KeyReturn = 36, KeyEnter = 76, KeySpace = 49;

    private DateOnly _displayMonth = new(DateOnly.FromDateTime(DateTime.Today).Year, DateOnly.FromDateTime(DateTime.Today).Month, 1);
    private IReadOnlySet<DateOnly> _selected = new HashSet<DateOnly>();
    private DayOfWeek _firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
    private bool _collapsed;
    private WinoIconGlyph _toggleGlyph = WinoIconGlyph.PanelLeftContract;
    private string? _toggleToolTip;
    private DateOnly _focused = DateOnly.FromDateTime(DateTime.Today);
    private readonly PressElement _previousElement;
    private readonly PressElement _nextElement;
    private readonly PressElement _toggleElement;
    private readonly PressElement[] _dayElements = new PressElement[Rows * 7];

    public event EventHandler<DateOnly>? DateClicked;
    public event EventHandler? ToggleClicked;

    public WinoMiniCalendarView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.GroupRole;
        _previousElement = new PressElement(this, _ => DisplayMonth = _displayMonth.AddMonths(-1));
        _nextElement = new PressElement(this, _ => DisplayMonth = _displayMonth.AddMonths(1));
        _toggleElement = new PressElement(this, _ => ToggleClicked?.Invoke(this, EventArgs.Empty));
        for (int i = 0; i < _dayElements.Length; i++)
            _dayElements[i] = new PressElement(this, element => ClickDay(element.Date));
        RebuildAccessibility();
        WinoStyle.AccentChanged += AccentChanged;
    }

    private void AccentChanged(object? sender, EventArgs args) => NeedsDisplay = true;

    public override bool IsFlipped => true;

    /// <summary>Any day inside the month to show.</summary>
    public DateOnly DisplayMonth
    {
        get => _displayMonth;
        set
        {
            var first = new DateOnly(value.Year, value.Month, 1);
            if (_displayMonth == first) return;
            _displayMonth = first;
            // Keep the focused day inside the shown month, on the same day number where possible.
            if (_focused.Year != first.Year || _focused.Month != first.Month)
                _focused = first.AddDays(Math.Min(_focused.Day, DateTime.DaysInMonth(first.Year, first.Month)) - 1);
            NeedsDisplay = true;
            NoteFocusRingMaskChanged();
            RebuildAccessibility();
        }
    }

    public IReadOnlySet<DateOnly> SelectedDates
    {
        get => _selected;
        set
        {
            _selected = value;
            NeedsDisplay = true;
            foreach (var element in _dayElements) element.AccessibilitySelected = _selected.Contains(element.Date);
        }
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => _firstDayOfWeek;
        set
        {
            if (_firstDayOfWeek == value) return;
            _firstDayOfWeek = value;
            NeedsDisplay = true;
            NoteFocusRingMaskChanged();
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
            NeedsDisplay = true;
            NoteFocusRingMaskChanged();
            RebuildAccessibility();
        }
    }

    public void SetToggle(WinoIconGlyph glyph, string? toolTip)
    {
        _toggleGlyph = glyph;
        _toggleToolTip = toolTip;
        ToolTip = toolTip;
        _toggleElement.AccessibilityLabel = toolTip ?? string.Empty;
        NeedsDisplay = true;
    }

    public static double HeightFor(bool collapsed) => collapsed ? Pad + HeaderHeight + Pad : Pad + HeaderHeight + 6 + NamesHeight + Rows * CellHeight + Pad;

    public override CGSize IntrinsicContentSize => new(NoIntrinsicMetric, (nfloat)HeightFor(_collapsed));

    private CGRect ToggleRect => new(Bounds.Width - Pad - 16, Pad + (HeaderHeight - 16) / 2, 16, 16);
    private CGRect PreviousRect => new(Bounds.Width - Pad - 16 - 8 - 16 - 8 - 16, Pad + (HeaderHeight - 16) / 2, 16, 16);
    private CGRect NextRect => new(Bounds.Width - Pad - 16 - 8 - 16, Pad + (HeaderHeight - 16) / 2, 16, 16);
    private double GridTop => Pad + HeaderHeight + 6 + NamesHeight;
    private double GridLeft => Pad;
    private double GridWidth => Bounds.Width - Pad * 2;
    private double ColumnWidth => GridWidth / 7;

    private DateOnly GridStart
    {
        get
        {
            int offset = ((int)_displayMonth.DayOfWeek - (int)_firstDayOfWeek + 7) % 7;
            return _displayMonth.AddDays(-offset);
        }
    }

    private CGRect CellRect(int index)
        => new(GridLeft + index % 7 * ColumnWidth, GridTop + index / 7 * CellHeight, ColumnWidth, CellHeight);

    public override void DrawRect(CGRect dirtyRect)
    {
        var dark = WinoIcons.IsDark(EffectiveAppearance);
        var text = NSColor.Label;
        var secondary = NSColor.SecondaryLabel;
        var tertiary = NSColor.TertiaryLabel;
        var accent = WinoStyle.Accent;

        var card = NSBezierPath.FromRoundedRect(Bounds, (nfloat)WinoStyle.GroupRadius, (nfloat)WinoStyle.GroupRadius);
        (dark ? WinoStyle.Hex(0xFFFFFF, 0.06) : WinoStyle.Hex(0xFFFFFF, 0.55)).SetFill();
        card.Fill();
        (dark ? WinoStyle.Hex(0xFFFFFF, 0.07) : WinoStyle.Hex(0x000000, 0.06)).SetStroke();
        card.LineWidth = 1;
        NSBezierPath.FromRoundedRect(Bounds.Inset(0.5f, 0.5f), (nfloat)WinoStyle.GroupRadius, (nfloat)WinoStyle.GroupRadius).Stroke();

        var culture = CultureInfo.CurrentCulture;
        var title = _displayMonth.ToString("MMMM yyyy", culture);
        DrawText(title, new CGRect(Pad, Pad, Bounds.Width - Pad * 2 - 80, HeaderHeight), NSFont.SystemFontOfSize(12, NSFontWeight.Semibold), text, NSTextAlignment.Left);
        WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.ChevronUp), PreviousRect.Inset(2, 2), secondary, EffectiveAppearance, false);
        WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.ChevronDown), NextRect.Inset(2, 2), secondary, EffectiveAppearance, false);
        WinoIcons.Draw(WinoIcons.Glyph(_toggleGlyph), ToggleRect.Inset(1, 1), secondary, EffectiveAppearance, false);
        if (_collapsed) return;

        var names = culture.DateTimeFormat.ShortestDayNames;
        for (int column = 0; column < 7; column++)
        {
            var day = (int)(_firstDayOfWeek + column) % 7;
            DrawText(names[day], new CGRect(GridLeft + column * ColumnWidth, Pad + HeaderHeight + 6, ColumnWidth, NamesHeight),
                NSFont.SystemFontOfSize(10), tertiary, NSTextAlignment.Center);
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var date = GridStart;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < 7; column++, date = date.AddDays(1))
            {
                var cell = new CGRect(GridLeft + column * ColumnWidth, GridTop + row * CellHeight, ColumnWidth, CellHeight);
                bool inMonth = date.Month == _displayMonth.Month;
                bool selected = _selected.Contains(date);
                bool isToday = date == today;
                if (selected)
                {
                    bool left = !_selected.Contains(date.AddDays(-1)) || column == 0;
                    bool right = !_selected.Contains(date.AddDays(1)) || column == 6;
                    var band = new CGRect(cell.X, cell.Y + 1, cell.Width, cell.Height - 2);
                    // Round only the ends of a contiguous run: extend the shape past a square side and clip to the cell.
                    nfloat radius = 6;
                    var shape = new CGRect(band.X - (left ? 0 : radius), band.Y, band.Width + (left ? 0 : radius) + (right ? 0 : radius), band.Height);
                    NSGraphicsContext.CurrentContext?.SaveGraphicsState();
                    NSBezierPath.FromRect(band).AddClip();
                    accent.ColorWithAlphaComponent(0.16f).SetFill();
                    NSBezierPath.FromRoundedRect(shape, radius, radius).Fill();
                    NSGraphicsContext.CurrentContext?.RestoreGraphicsState();
                }
                if (isToday)
                {
                    var circle = new CGRect(cell.GetMidX() - 11, cell.GetMidY() - 11, 22, 22);
                    accent.SetFill();
                    NSBezierPath.FromOvalInRect(circle).Fill();
                }
                var color = isToday ? NSColor.White : inMonth ? text : tertiary;
                var font = isToday ? NSFont.SystemFontOfSize(11, NSFontWeight.Semibold) : NSFont.SystemFontOfSize(11);
                DrawText(date.Day.ToString(culture), cell, font, color, NSTextAlignment.Center);
            }
        }
    }

    private static void DrawText(string value, CGRect rect, NSFont font, NSColor color, NSTextAlignment alignment)
    {
        var paragraph = new NSMutableParagraphStyle { Alignment = alignment, LineBreakMode = NSLineBreakMode.TruncatingTail };
        var attributes = new NSStringAttributes { Font = font, ForegroundColor = color, ParagraphStyle = paragraph };
        var text = new NSAttributedString(value, attributes);
        var size = text.Size;
        var target = new CGRect(rect.X, rect.Y + (rect.Height - size.Height) / 2, rect.Width, size.Height);
        text.DrawInRect(target);
    }

    public override void MouseDown(NSEvent theEvent)
    {
        var point = ConvertPointFromView(theEvent.LocationInWindow, null);
        if (ToggleRect.Inset(-4, -4).Contains(point)) { ToggleClicked?.Invoke(this, EventArgs.Empty); return; }
        if (PreviousRect.Inset(-4, -4).Contains(point)) { DisplayMonth = _displayMonth.AddMonths(-1); return; }
        if (NextRect.Inset(-4, -4).Contains(point)) { DisplayMonth = _displayMonth.AddMonths(1); return; }
        if (_collapsed || point.Y < GridTop) return;
        int row = (int)((point.Y - GridTop) / CellHeight);
        int column = (int)((point.X - GridLeft) / ColumnWidth);
        if (row < 0 || row >= Rows || column < 0 || column > 6) return;
        ClickDay(GridStart.AddDays(row * 7 + column));
    }

    /// <summary>A pointer or VoiceOver press on a day: focus follows without paging, then the date is selected.</summary>
    private void ClickDay(DateOnly date)
    {
        _focused = date;
        NoteFocusRingMaskChanged();
        SelectDate(date);
    }

    private void SelectDate(DateOnly date) => DateClicked?.Invoke(this, date);

    /// <summary>Moves keyboard focus, paging the month when the date leaves it.</summary>
    private void FocusDate(DateOnly date)
    {
        _focused = date;
        DisplayMonth = date;
        NoteFocusRingMaskChanged();
    }

    public override bool AcceptsFirstResponder() => true;

    public override void KeyDown(NSEvent theEvent)
    {
        // Leave shortcuts (Command/Control/Option chords) to the menu and the responder chain.
        if ((theEvent.ModifierFlags & (NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask | NSEventModifierMask.AlternateKeyMask)) != 0)
        {
            base.KeyDown(theEvent);
            return;
        }
        switch (theEvent.KeyCode)
        {
            case KeyPageUp: FocusDate(_focused.AddMonths(-1)); return;
            case KeyPageDown: FocusDate(_focused.AddMonths(1)); return;
            case KeyHome: FocusDate(DateOnly.FromDateTime(DateTime.Today)); return;
        }
        if (!_collapsed)
        {
            switch (theEvent.KeyCode)
            {
                case KeyLeft: FocusDate(_focused.AddDays(-1)); return;
                case KeyRight: FocusDate(_focused.AddDays(1)); return;
                case KeyArrowUp: FocusDate(_focused.AddDays(-7)); return;
                case KeyArrowDown: FocusDate(_focused.AddDays(7)); return;
                case KeyReturn or KeyEnter or KeySpace: SelectDate(_focused); return;
            }
        }
        base.KeyDown(theEvent);
    }

    public override CGRect FocusRingMaskBounds => Bounds;

    /// <summary>The system focus ring traces the focused day, or the card while the grid is hidden.</summary>
    public override void DrawFocusRingMask()
    {
        int index = _focused.DayNumber - GridStart.DayNumber;
        if (_collapsed || index < 0 || index >= Rows * 7)
        {
            NSBezierPath.FromRoundedRect(Bounds, (nfloat)WinoStyle.GroupRadius, (nfloat)WinoStyle.GroupRadius).Fill();
            return;
        }
        var cell = CellRect(index);
        NSBezierPath.FromRoundedRect(new CGRect(cell.X + 1, cell.Y + 1, cell.Width - 2, cell.Height - 2), 6, 6).Fill();
    }

    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        UpdateAccessibilityFrames();
        NoteFocusRingMaskChanged();
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
                element.AccessibilitySelected = _selected.Contains(date);
                children.Add(element);
                date = date.AddDays(1);
            }
        }
        AccessibilityChildren = children.ToArray();
        UpdateAccessibilityFrames();
    }

    private void UpdateAccessibilityFrames()
    {
        _previousElement.AccessibilityFrameInParentSpace = PreviousRect;
        _nextElement.AccessibilityFrameInParentSpace = NextRect;
        _toggleElement.AccessibilityFrameInParentSpace = ToggleRect;
        for (int i = 0; i < _dayElements.Length; i++) _dayElements[i].AccessibilityFrameInParentSpace = CellRect(i);
    }

    /// <summary>A pressable accessibility child standing in for a drawn hit region.</summary>
    private sealed class PressElement : NSAccessibilityElement
    {
        private readonly Action<PressElement> _press;

        public PressElement(NSView parent, Action<PressElement> press)
        {
            _press = press;
            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.ButtonRole;
            AccessibilityParent = parent;
        }

        public DateOnly Date { get; set; }

        public override bool AccessibilityPerformPress() { _press(this); return true; }
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }
}
