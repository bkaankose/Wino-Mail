using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.MailList;

/// <summary>
/// Capsule chip used in mail rows (18 pt: attachment count, category, folder) and in the
/// search scope bar (22 pt: filter tokens). A chip can carry a Wino glyph or a colour dot,
/// and becomes a button when <see cref="IsClickable"/> is set. Category tiles use a 4pt radius
/// (set <see cref="WinoSurfaceView.CornerRadius"/>); the default shape is a capsule.
/// </summary>
public class WinoChipView : WinoSurfaceView
{
    private readonly WinoIconView _glyph;
    private NSLayoutConstraint? _maxTextWidth;
    private readonly WinoSurfaceView _dot;
    private readonly NSTextField _label;
    private readonly NSTextField _prefix;
    private readonly NSLayoutConstraint _height;
    private NSColor _textColor = WinoStyle.SecondaryText;

    public WinoChipView(double height = 18)
    {
        _glyph = new WinoIconView(WinoIconGlyph.None, 10) { Hidden = true };
        _dot = new WinoSurfaceView { Hidden = true, CornerRadius = 3 };
        WinoLayout.Size(_dot, 6, 6);
        _prefix = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(11, NSFontWeight.Semibold));
        _prefix.Hidden = true;
        _label = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(11, NSFontWeight.Medium));
        _label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var row = WinoLayout.HStack(4, _dot, _glyph, _prefix, _label);
        row.EdgeInsets = new NSEdgeInsets(0, 7, 0, 7);
        WinoLayout.Fill(row, this);
        _height = HeightAnchor.ConstraintEqualTo((nfloat)height);
        _height.Active = true;
        CornerRadius = height / 2;
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        Fill = WinoStyle.SubtleFill;
        ApplyTextColor();
    }

    public string Text
    {
        get => _label.StringValue;
        set { _label.StringValue = value ?? string.Empty; AccessibilityLabel = string.IsNullOrEmpty(Prefix) ? value : $"{Prefix} {value}"; }
    }

    /// <summary>Bold leading text, used by value-carrying filter tokens ("Account" bkaan@…).</summary>
    public string? Prefix
    {
        get => _prefix.Hidden ? null : _prefix.StringValue;
        set { _prefix.StringValue = value ?? string.Empty; _prefix.Hidden = string.IsNullOrEmpty(value); }
    }

    /// <summary>Shows a Wino glyph (font character) before the text; null or empty hides it.</summary>
    public void SetGlyph(string? glyph, double pointSize = 10)
    {
        _glyph.PointSize = pointSize;
        _glyph.Glyph = glyph ?? string.Empty;
        _glyph.Hidden = string.IsNullOrEmpty(glyph);
    }

    public void SetGlyph(WinoIconGlyph icon, double pointSize = 10) => SetGlyph(icon == WinoIconGlyph.None ? null : WinoIcons.Glyph(icon), pointSize);

    /// <summary>Caps the text width (Windows caps intelligence tile text at 180); 0 removes the cap.</summary>
    public double MaxTextWidth
    {
        get => _maxTextWidth?.Constant ?? 0;
        set
        {
            _maxTextWidth?.Dispose();
            _maxTextWidth = null;
            if (value <= 0) return;
            _maxTextWidth = _label.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)value);
            _maxTextWidth.Active = true;
        }
    }

    /// <summary>Tint of the glyph; defaults to the text colour.</summary>
    public NSColor? GlyphTint
    {
        get => _glyph.Tint;
        set => _glyph.Tint = value ?? _textColor;
    }

    public NSColor? DotColor
    {
        get => _dot.Hidden ? null : _dot.Fill;
        set { _dot.Fill = value; _dot.Hidden = value is null; }
    }

    public NSColor TextColor
    {
        get => _textColor;
        set { _textColor = value; ApplyTextColor(); }
    }

    public double ChipHeight
    {
        get => _height.Constant;
        set { _height.Constant = (nfloat)value; CornerRadius = value / 2; }
    }

    private void ApplyTextColor()
    {
        _label.TextColor = _textColor;
        _prefix.TextColor = _textColor;
        _glyph.Tint = _textColor;
    }

    public object? Tag { get; set; }

    public bool IsClickable { get; set; }

    public event EventHandler? Clicked;

    public override bool AcceptsFirstMouse(NSEvent? theEvent) => IsClickable;

    public override void MouseDown(NSEvent theEvent)
    {
        if (!IsClickable) base.MouseDown(theEvent);
    }

    public override void MouseUp(NSEvent theEvent)
    {
        if (!IsClickable) { base.MouseUp(theEvent); return; }
        var point = ConvertPointFromView(theEvent.LocationInWindow, null);
        if (Bounds.Contains(point)) Clicked?.Invoke(this, EventArgs.Empty);
    }

    public override bool AccessibilityPerformPress()
    {
        if (!IsClickable) return false;
        Clicked?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public override CGSize IntrinsicContentSize => new(NSView.NoIntrinsicMetric, (nfloat)_height.Constant);

    protected override void Dispose(bool disposing)
    {
        if (disposing) Clicked = null;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Lays out its subviews left to right and wraps them onto new lines, like a CSS flex-wrap row.
/// Children keep their fitting size. Used for chip rows that must not clip. Fitting sizes are measured once
/// per item change (<see cref="ItemsChanged"/>) and reused by layout and intrinsic-size passes.
/// </summary>
public sealed class WinoFlowView : NSView
{
    private nfloat _lastHeight;
    private CGSize[]? _sizes;

    public WinoFlowView() => TranslatesAutoresizingMaskIntoConstraints = false;

    public double Spacing { get; set; } = 6;
    public double LineSpacing { get; set; } = 6;

    public override bool IsFlipped => true;

    public void SetItems(IEnumerable<NSView> views)
    {
        foreach (var view in Subviews) view.RemoveFromSuperview();
        foreach (var view in views)
        {
            view.TranslatesAutoresizingMaskIntoConstraints = true;
            AddSubview(view);
        }
        ItemsChanged();
    }

    /// <summary>Appends one item; call <see cref="ItemsChanged"/> after a batch of changes.</summary>
    public void AddItem(NSView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = true;
        AddSubview(view);
        _sizes = null;
    }

    /// <summary>Re-flows the items after they were added, removed or resized in place.</summary>
    public void ItemsChanged()
    {
        _sizes = null;
        NeedsLayout = true;
        InvalidateIntrinsicContentSize();
    }

    private nfloat Arrange(nfloat width, bool apply)
    {
        var subviews = Subviews;
        if (_sizes is null || _sizes.Length != subviews.Length) _sizes = subviews.Select(static view => view.FittingSize).ToArray();
        nfloat x = 0, y = 0, line = 0;
        for (int index = 0; index < subviews.Length; index++)
        {
            var view = subviews[index];
            var size = _sizes[index];
            if (x > 0 && x + size.Width > width)
            {
                x = 0;
                y += line + (nfloat)LineSpacing;
                line = 0;
            }
            if (apply) view.Frame = new CGRect(x, y, (nfloat)Math.Min((double)size.Width, (double)width), size.Height);
            x += size.Width + (nfloat)Spacing;
            line = (nfloat)Math.Max((double)line, (double)size.Height);
        }
        return subviews.Length == 0 ? 0 : y + line;
    }

    public override void Layout()
    {
        base.Layout();
        var height = Arrange(Bounds.Width, true);
        if (height != _lastHeight)
        {
            _lastHeight = height;
            InvalidateIntrinsicContentSize();
        }
    }

    public override CGSize IntrinsicContentSize
        => new(NSView.NoIntrinsicMetric, Bounds.Width > 0 ? Arrange(Bounds.Width, false) : (Subviews.Length == 0 ? 0 : 22));
}
