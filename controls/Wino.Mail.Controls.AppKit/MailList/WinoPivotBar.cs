using AppKit;
using CoreGraphics;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.MailList;

/// <summary>One folder pivot: its title and the selected-item count shown as "(n)" when positive.</summary>
public sealed record WinoPivotItem(string Title, int Count);

/// <summary>
/// The mail list's folder pivot (Windows PivotSegmentedStyle): text segments 30pt tall with a
/// 2pt accent underline under the selected one, e.g. "Focused (2) | Other". Clicking a segment
/// raises <see cref="SelectionChanged"/>.
/// </summary>
public sealed class WinoPivotBar : NSView
{
    private readonly NSStackView _stack;
    private readonly List<Segment> _segments = new();
    private int _selectedIndex = -1;

    public WinoPivotBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _stack = WinoLayout.HStack(16);
        _stack.Alignment = NSLayoutAttribute.Bottom;
        _stack.SetClippingResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        WinoLayout.Fill(_stack, this);
        HeightAnchor.ConstraintEqualTo(30).Active = true;
        SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        WinoStyle.AccentChanged += AccentChanged;
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.TabGroupRole;
    }

    /// <summary>Raised with the index the user clicked; the owner sets <see cref="SelectedIndex"/> back.</summary>
    public event EventHandler<int>? SelectionChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            _selectedIndex = value;
            for (int index = 0; index < _segments.Count; index++) _segments[index].IsSelected = index == value;
        }
    }

    public void SetItems(IReadOnlyList<WinoPivotItem> items, int selectedIndex)
    {
        while (_segments.Count > items.Count)
        {
            var last = _segments[^1];
            _segments.RemoveAt(_segments.Count - 1);
            _stack.RemoveArrangedSubview(last);
            last.RemoveFromSuperview();
            last.Dispose();
        }
        while (_segments.Count < items.Count)
        {
            int index = _segments.Count;
            var segment = new Segment(() => SelectionChanged?.Invoke(this, index));
            _segments.Add(segment);
            _stack.AddArrangedSubview(segment);
        }
        for (int index = 0; index < items.Count; index++) _segments[index].Set(items[index]);
        SelectedIndex = selectedIndex;
    }

    private void AccentChanged(object? sender, EventArgs args)
    {
        foreach (var segment in _segments) segment.NeedsDisplay = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WinoStyle.AccentChanged -= AccentChanged;
            SelectionChanged = null;
        }
        base.Dispose(disposing);
    }

    private sealed class Segment : NSView
    {
        private readonly Action _click;
        private readonly NSTextField _label;
        private bool _selected;
        private bool _pressed;

        public Segment(Action click)
        {
            _click = click;
            TranslatesAutoresizingMaskIntoConstraints = false;
            _label = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
            AddSubview(_label);
            NSLayoutConstraint.ActivateConstraints(
            [
                _label.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 2),
                _label.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -2),
                _label.CenterYAnchor.ConstraintEqualTo(CenterYAnchor, -1),
                HeightAnchor.ConstraintEqualTo(30)
            ]);
            SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.RadioButtonRole;
        }

        public bool IsSelected
        {
            set
            {
                _selected = value;
                _label.Font = NSFont.SystemFontOfSize(13, value ? NSFontWeight.Semibold : NSFontWeight.Regular);
                _label.TextColor = value ? WinoStyle.PrimaryText : WinoStyle.SecondaryText;
                AccessibilityValue = Foundation.NSNumber.FromBoolean(value);
                NeedsDisplay = true;
            }
        }

        public void Set(WinoPivotItem item)
        {
            _label.StringValue = item.Count > 0 ? $"{item.Title} ({item.Count})" : item.Title;
            AccessibilityLabel = _label.StringValue;
        }

        public override bool IsFlipped => true;

        public override void DrawRect(CGRect dirtyRect)
        {
            if (!_selected) return;
            WinoStyle.Accent.SetFill();
            NSBezierPath.FromRoundedRect(new CGRect(0, Bounds.Height - 2, Bounds.Width, 2), 1, 1).Fill();
        }

        public override void MouseDown(NSEvent theEvent) => _pressed = true;

        public override void MouseUp(NSEvent theEvent)
        {
            if (_pressed && Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) _click();
            _pressed = false;
        }

        public override bool AccessibilityPerformPress()
        {
            _click();
            return true;
        }
    }
}
