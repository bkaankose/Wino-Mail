using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// A flat toolbar button drawn like the Windows CalendarTitleBarContent buttons: a Wino glyph,
/// an optional label, a card fill, and the accent fill with white content when checked.
/// </summary>
internal sealed class CalendarToolbarButton : WinoPressableView
{
    private static readonly NSColor CardFill = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.72), WinoStyle.Hex(0xFFFFFF, 0.08));
    private static readonly NSColor HoverFill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.12));
    private readonly WinoIconView _icon;
    private readonly NSTextField? _label;
    private readonly bool _card;
    private bool _checked;
    private bool _hover;

    public CalendarToolbarButton(WinoIconGlyph glyph, double glyphSize, string? title, bool card, string accessibilityLabel)
    {
        _card = card;
        CornerRadius = 5;
        _icon = new WinoIconView(glyph, glyphSize);
        if (title is null)
        {
            // Icon-only buttons are sized by the owner; centre the glyph directly.
            AddSubview(_icon);
            NSLayoutConstraint.ActivateConstraints(
            [
                _icon.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
                _icon.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
            ]);
        }
        else
        {
            _label = WinoStyle.Label(title, NSFont.SystemFontOfSize(13));
            var stack = WinoLayout.HStack(6, _icon, _label);
            AddSubview(stack);
            NSLayoutConstraint.ActivateConstraints(
            [
                stack.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 10),
                stack.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -10),
                stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
            ]);
        }
        AccessibilityLabel = accessibilityLabel;
        ToolTip = accessibilityLabel;
        Apply();
    }

    public event EventHandler? Activated;

    public bool Checked
    {
        get => _checked;
        set { _checked = value; Apply(); }
    }

    private void Apply()
    {
        Fill = _checked ? WinoStyle.Accent : _hover ? HoverFill : _card ? CardFill : null;
        var tint = _checked ? NSColor.White : null;
        _icon.Tint = tint;
        _icon.Colorful = _checked ? false : null;
        if (_label is not null) _label.TextColor = tint ?? WinoStyle.PrimaryText;
    }

    /// <summary>Re-reads the accent after a theme change.</summary>
    public void Refresh() => Apply();

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        foreach (var area in TrackingAreas()) RemoveTrackingArea(area);
        AddTrackingArea(new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null));
    }

    public override void MouseEntered(NSEvent theEvent) { _hover = true; Apply(); }
    public override void MouseExited(NSEvent theEvent) { _hover = false; Apply(); }

    protected override void OnActivated() => Activated?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The Windows CalendarTitleBarContent: previous/next (128pt column), the visible range text at
/// 18pt and the WinoCalendarTypeSelectorControl (Today, then Day / Week / Work week / Month).
/// Margin 4,0,7,8 like Windows. The page wires the events to the calendar shell commands.
/// </summary>
internal sealed class CalendarToolbarView : NSView
{
    private static readonly NSColor SelectorFill = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.55), WinoStyle.Hex(0xFFFFFF, 0.06));
    private readonly NSTextField _rangeText;
    private readonly Dictionary<CalendarDisplayType, CalendarToolbarButton> _segments = new();
    private readonly List<CalendarToolbarButton> _all = new();

    public CalendarToolbarView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        // No translation keys exist for the navigation button names (Mac-only accessibility labels).
        var previous = Button(WinoIconGlyph.ArrowLeft, 12, null, true, "Previous");
        var next = Button(WinoIconGlyph.ArrowRight, 12, null, true, "Next");
        WinoLayout.Size(previous, 61, 32);
        WinoLayout.Size(next, 61, 32);
        previous.Activated += (_, _) => PreviousRequested?.Invoke(this, EventArgs.Empty);
        next.Activated += (_, _) => NextRequested?.Invoke(this, EventArgs.Empty);
        var navigation = WinoLayout.HStack(6, previous, next);

        _rangeText = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(18, NSFontWeight.Semibold));
        _rangeText.SetContentCompressionResistancePriority(260, NSLayoutConstraintOrientation.Horizontal);
        _rangeText.SetContentHuggingPriorityForOrientation(751, NSLayoutConstraintOrientation.Horizontal);

        var today = Button(WinoIconGlyph.CalendarToday, 15, Translator.Today, false, Translator.Today);
        today.Activated += (_, _) => TodayRequested?.Invoke(this, EventArgs.Empty);
        var divider = new WinoSurfaceView { Fill = WinoStyle.ZoneStroke };
        WinoLayout.Size(divider, 1, 22);
        var day = Segment(CalendarDisplayType.Day, WinoIconGlyph.CalendarDay, Translator.CalendarTypeSelector_Day);
        var week = Segment(CalendarDisplayType.Week, WinoIconGlyph.CalendarWeek, Translator.CalendarTypeSelector_Week);
        var workWeek = Segment(CalendarDisplayType.WorkWeek, WinoIconGlyph.CalendarWorkWeek, Translator.CalendarTypeSelector_WorkWeek);
        var month = Segment(CalendarDisplayType.Month, WinoIconGlyph.CalendarMonth, Translator.CalendarTypeSelector_Month);
        foreach (var segment in new[] { today, day, week, workWeek, month }) WinoLayout.Size(segment, -1, 30);

        var selectorStack = WinoLayout.HStack(2, today, divider, day, week, workWeek, month);
        selectorStack.EdgeInsets = new NSEdgeInsets(3, 3, 3, 3);
        var selector = new WinoSurfaceView { Fill = SelectorFill, Stroke = WinoStyle.ZoneStroke, CornerRadius = 7 };
        WinoLayout.Fill(selectorStack, selector);
        selector.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        navigation.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);

        var row = WinoLayout.HStack(16, navigation, _rangeText, WinoLayout.Spacer(), selector);
        row.EdgeInsets = new NSEdgeInsets(0, 4, 8, 7);
        WinoLayout.Fill(row, this);
        WinoStyle.AccentChanged += AccentChanged;
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

    public CalendarDisplayType SelectedType
    {
        set { foreach (var (type, button) in _segments) button.Checked = type == value; }
    }

    private CalendarToolbarButton Button(WinoIconGlyph glyph, double size, string? title, bool card, string label)
    {
        var button = new CalendarToolbarButton(glyph, size, title, card, label);
        _all.Add(button);
        return button;
    }

    private CalendarToolbarButton Segment(CalendarDisplayType type, WinoIconGlyph glyph, string title)
    {
        var button = Button(glyph, 15, title, false, title);
        button.Activated += (_, _) => DisplayTypeRequested?.Invoke(this, type);
        _segments[type] = button;
        return button;
    }

    private void AccentChanged(object? sender, EventArgs args)
    {
        foreach (var button in _all) button.Refresh();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }
}
