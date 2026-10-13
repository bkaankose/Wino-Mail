using CoreGraphics;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.Layout;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.MailList;

/// <summary>
/// Capsule chip (UIKit twin of the AppKit <c>WinoChipView</c>) used in mail rows (18pt: category and intelligence
/// tiles) and the search scope bar (filter tokens). A chip carries an optional Wino glyph or colour dot and an optional
/// bold prefix, and becomes a button when <see cref="IsClickable"/> is set. Category tiles use a 4pt radius; the default
/// shape is a capsule.
/// </summary>
public class WinoChipView : WinoSurfaceView
{
    private readonly WinoIconView _glyph;
    private readonly WinoSurfaceView _dot;
    private readonly UILabel _label;
    private readonly UILabel _prefix;
    private readonly NSLayoutConstraint _height;
    private NSLayoutConstraint? _maxTextWidth;
    private UIColor _textColor = WinoStyle.SecondaryText;
    private bool _isClickable;
    private UITapGestureRecognizer? _tap;

    public WinoChipView(double height = 18)
    {
        _glyph = new WinoIconView(WinoIconGlyph.None, 10) { Hidden = true };
        _dot = new WinoSurfaceView { Hidden = true, CornerRadius = 3 };
        WinoLayout.Size(_dot, 6, 6);
        _prefix = WinoStyle.Label(string.Empty, WinoStyle.Fixed(11, UIFontWeight.Semibold));
        _prefix.Hidden = true;
        _label = WinoStyle.Label(string.Empty, WinoStyle.Fixed(11, UIFontWeight.Medium));
        _label.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        var row = WinoLayout.Insets(WinoLayout.HStack(4, _dot, _glyph, _prefix, _label), 0, 7, 0, 7);
        WinoLayout.Fill(row, this);
        _height = HeightAnchor.ConstraintEqualTo((nfloat)height);
        _height.Active = true;
        CornerRadius = height / 2;
        SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        Fill = WinoStyle.SubtleFill;
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.StaticText;
        ApplyTextColor();
    }

    public string Text
    {
        get => _label.Text ?? string.Empty;
        set
        {
            _label.Text = value ?? string.Empty;
            AccessibilityLabel = string.IsNullOrEmpty(Prefix) ? value : $"{Prefix} {value}";
        }
    }

    /// <summary>Bold leading text, used by value-carrying filter tokens ("Account" bkaan@…).</summary>
    public string? Prefix
    {
        get => _prefix.Hidden ? null : _prefix.Text;
        set { _prefix.Text = value ?? string.Empty; _prefix.Hidden = string.IsNullOrEmpty(value); }
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
            if (_maxTextWidth is not null) _maxTextWidth.Active = false;
            _maxTextWidth = null;
            if (value <= 0) return;
            _maxTextWidth = _label.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)value);
            _maxTextWidth.Active = true;
        }
    }

    /// <summary>Tint of the glyph; defaults to the text colour.</summary>
    public UIColor? GlyphTint
    {
        get => _glyph.Tint;
        set => _glyph.Tint = value ?? _textColor;
    }

    public UIColor? DotColor
    {
        get => _dot.Hidden ? null : _dot.Fill;
        set { _dot.Fill = value; _dot.Hidden = value is null; }
    }

    public UIColor TextColor
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

    public new object? Tag { get; set; }

    /// <summary>A clickable chip raises <see cref="Clicked"/> on a tap and reads as a button.</summary>
    public bool IsClickable
    {
        get => _isClickable;
        set
        {
            if (_isClickable == value) return;
            _isClickable = value;
            if (value)
            {
                _tap ??= new UITapGestureRecognizer(Tapped);
                AddGestureRecognizer(_tap);
                AccessibilityTraits |= UIAccessibilityTrait.Button;
            }
            else if (_tap is not null)
            {
                RemoveGestureRecognizer(_tap);
                AccessibilityTraits &= ~UIAccessibilityTrait.Button;
            }
        }
    }

    public event EventHandler? Clicked;

    private void Tapped() => Clicked?.Invoke(this, EventArgs.Empty);

    public override bool AccessibilityActivate()
    {
        if (!_isClickable) return false;
        Clicked?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The fitting size the flow layout measures.</summary>
    public CGSize FittingSize => SystemLayoutSizeFittingSize(UILayoutFittingCompressedSize);

    public override CGSize IntrinsicContentSize => new(NoIntrinsicMetric, _height.Constant);

    protected override void Dispose(bool disposing)
    {
        if (disposing) Clicked = null;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Lays out its subviews left to right and wraps them onto new lines (UIKit twin of the AppKit <c>WinoFlowView</c>,
/// shared <see cref="WinoFlowLayout"/>). Children keep their fitting size; fitting sizes are measured once per item
/// change (<see cref="ItemsChanged"/>) and reused by layout and intrinsic-size passes.
/// </summary>
public sealed class WinoFlowView : UIView
{
    private nfloat _lastHeight;
    private nfloat _lastWidth;
    private CGSize[]? _sizes;

    public WinoFlowView() => TranslatesAutoresizingMaskIntoConstraints = false;

    public double Spacing { get; set; } = 6;
    public double LineSpacing { get; set; } = 6;

    public void SetItems(IEnumerable<UIView> views)
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
    public void AddItem(UIView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = true;
        AddSubview(view);
        _sizes = null;
    }

    /// <summary>Re-flows the items after they were added, removed or resized in place.</summary>
    public void ItemsChanged()
    {
        _sizes = null;
        SetNeedsLayout();
        InvalidateIntrinsicContentSize();
    }

    /// <summary>Height of the flowed items in a row <paramref name="width"/> wide.</summary>
    public nfloat HeightFor(nfloat width) => Arrange(width, false);

    private nfloat Arrange(nfloat width, bool apply)
    {
        var subviews = Subviews;
        if (_sizes is null || _sizes.Length != subviews.Length)
            _sizes = subviews.Select(static view => view is WinoChipView chip ? chip.FittingSize : view.SystemLayoutSizeFittingSize(UILayoutFittingCompressedSize)).ToArray();
        var sizes = new (double Width, double Height)[_sizes.Length];
        for (int index = 0; index < sizes.Length; index++) sizes[index] = (_sizes[index].Width, _sizes[index].Height);
        var result = WinoFlowLayout.Arrange(sizes, width, Spacing, LineSpacing);
        if (apply)
        {
            for (int index = 0; index < subviews.Length; index++)
            {
                var frame = result.Frames[index];
                subviews[index].Frame = new CGRect(frame.X, frame.Y, frame.Width, frame.Height);
            }
        }
        return (nfloat)result.Height;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var height = Arrange(Bounds.Width, true);
        if (height != _lastHeight || Bounds.Width != _lastWidth)
        {
            _lastHeight = height;
            _lastWidth = Bounds.Width;
            InvalidateIntrinsicContentSize();
        }
    }

    public override CGSize IntrinsicContentSize
        => new(NoIntrinsicMetric, Bounds.Width > 0 ? Arrange(Bounds.Width, false) : (Subviews.Length == 0 ? 0 : 22));
}
