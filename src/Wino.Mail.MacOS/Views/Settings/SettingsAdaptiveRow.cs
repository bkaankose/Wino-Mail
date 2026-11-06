using AppKit;
using CoreGraphics;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// A settings row that lays its views side by side while it is at least <c>breakpoint</c> wide, else stacks them, and
/// can drop a decorative view when stacked. The breakpoint sits above the side-by-side minimum width, so the row switches
/// before it would hold the Settings window open, which it otherwise does even while hidden.
/// </summary>
public sealed class SettingsAdaptiveRow : NSView
{
    private readonly NSStackView _stack;
    private readonly double _breakpoint;
    private readonly double _spacing;
    private readonly NSLayoutAttribute _wideAlignment;
    private readonly NSView? _hiddenWhenNarrow;
    private bool? _wide;

    public SettingsAdaptiveRow(double breakpoint, double spacing, NSLayoutAttribute wideAlignment, NSView? hiddenWhenNarrow, params NSView[] views)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _breakpoint = breakpoint;
        _spacing = spacing;
        _wideAlignment = wideAlignment;
        _hiddenWhenNarrow = hiddenWhenNarrow;
        _stack = WinoLayout.HStack(spacing, views);
        WinoLayout.Fill(_stack, this);
        Apply(wide: false);
    }

    /// <summary>The Wino Intelligence purchase row: illustration, text column and the optional button column.</summary>
    public static SettingsAdaptiveRow IntelligencePromo(NSView art, NSView column, NSView? side, double breakpoint)
        => side is null
            ? new SettingsAdaptiveRow(breakpoint, 20, NSLayoutAttribute.CenterY, art, art, column)
            : new SettingsAdaptiveRow(breakpoint, 20, NSLayoutAttribute.CenterY, art, art, column, side);

    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        Apply(newSize.Width >= _breakpoint);
    }

    private void Apply(bool wide)
    {
        if (_wide == wide) return;
        _wide = wide;
        _stack.Orientation = wide ? NSUserInterfaceLayoutOrientation.Horizontal : NSUserInterfaceLayoutOrientation.Vertical;
        _stack.Alignment = wide ? _wideAlignment : NSLayoutAttribute.Leading;
        _stack.Spacing = (nfloat)(wide ? _spacing : Math.Max(_spacing, 12));
        if (_hiddenWhenNarrow is not null) _hiddenWhenNarrow.Hidden = !wide;
    }
}
