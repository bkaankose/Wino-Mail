using AppKit;
using CoreGraphics;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// A settings row that lays its views side by side while it is at least <c>breakpoint</c> wide, else stacks them, can drop a
/// decorative view when stacked and stretches one view to the row width when stacked. Side by side, the views share the
/// leftover width with the stack's fill distribution. The breakpoint sits above the side-by-side minimum width, so the row switches
/// before it would hold the Settings window open, which it otherwise does even while hidden.
/// </summary>
public sealed class SettingsAdaptiveRow : NSView
{
    private readonly NSStackView _stack;
    private readonly double _breakpoint;
    private readonly double _spacing;
    private readonly NSLayoutAttribute _wideAlignment;
    private readonly NSView? _hiddenWhenNarrow;
    private readonly NSLayoutConstraint? _stackedWidth;
    private bool? _wide;

    public SettingsAdaptiveRow(double breakpoint, double spacing, NSLayoutAttribute wideAlignment, NSView? hiddenWhenNarrow, NSView? stretchWhenStacked, params NSView[] views)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _breakpoint = breakpoint;
        _spacing = spacing;
        _wideAlignment = wideAlignment;
        _hiddenWhenNarrow = hiddenWhenNarrow;
        _stack = WinoLayout.HStack(spacing, views);
        _stack.Distribution = NSStackViewDistribution.Fill;
        WinoLayout.Fill(_stack, this);
        if (stretchWhenStacked is not null)
            _stackedWidth = stretchWhenStacked.WidthAnchor.ConstraintEqualTo(_stack.WidthAnchor);
        Apply(wide: false);
    }

    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        Apply(newSize.Width >= _breakpoint);
    }

    private void Apply(bool wide)
    {
        if (_wide == wide) return;
        _wide = wide;
        if (_stackedWidth is not null) _stackedWidth.Active = !wide;
        _stack.Orientation = wide ? NSUserInterfaceLayoutOrientation.Horizontal : NSUserInterfaceLayoutOrientation.Vertical;
        _stack.Alignment = wide ? _wideAlignment : NSLayoutAttribute.Leading;
        _stack.Spacing = (nfloat)(wide ? _spacing : Math.Max(_spacing, 12));
        if (_hiddenWhenNarrow is not null) _hiddenWhenNarrow.Hidden = !wide;
    }
}
