using CoreGraphics;
using UIKit;
using Wino.Mail.Controls.Core;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Common;

/// <summary>
/// Circular avatar (UIKit twin of the AppKit <c>WinoContactPicture</c>): a picture when one is set, otherwise initials
/// on a colour from the Wino avatar palette keyed by the address (<see cref="ContactPictureAppearance"/>). Set
/// <see cref="IsSquare"/> for account pictures (rounded square).
/// </summary>
public sealed class WinoContactPicture : WinoSurfaceView
{
    private readonly UILabel _initials;
    private readonly UIImageView _image;
    private readonly NSLayoutConstraint _width;
    private readonly NSLayoutConstraint _height;
    private double _size;
    private bool _isSquare;

    public WinoContactPicture(double size = 32)
    {
        _initials = WinoStyle.Label(string.Empty, WinoStyle.Fixed(13, UIFontWeight.Semibold), UIColor.White);
        _initials.TextAlignment = UITextAlignment.Center;
        _initials.AdjustsFontForContentSizeCategory = false;
        _image = new UIImageView { ContentMode = UIViewContentMode.ScaleAspectFill, ClipsToBounds = true, Hidden = true };
        AddSubview(_initials);
        WinoLayout.Fill(_image, this);
        NSLayoutConstraint.ActivateConstraints(
        [
            _initials.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _initials.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _initials.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 2),
            _initials.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -2)
        ]);
        _width = WidthAnchor.ConstraintEqualTo((nfloat)size);
        _height = HeightAnchor.ConstraintEqualTo((nfloat)size);
        _width.Active = _height.Active = true;
        SetContentHuggingPriority(1000, UILayoutConstraintAxis.Horizontal);
        SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Image;
        Size = size;
    }

    public double Size
    {
        get => _size;
        set
        {
            _size = value;
            _width.Constant = _height.Constant = (nfloat)value;
            _initials.Font = WinoStyle.Fixed(Math.Max(9, Math.Round(value * 0.38)), UIFontWeight.Semibold);
            UpdateShape();
            InvalidateIntrinsicContentSize();
        }
    }

    public bool IsSquare
    {
        get => _isSquare;
        set { _isSquare = value; UpdateShape(); }
    }

    /// <summary>Shows initials derived from <paramref name="displayName"/> on a colour derived from <paramref name="colorKey"/>.</summary>
    public void SetIdentity(string? displayName, string? colorKey, UIColor? colorOverride = null)
    {
        _initials.Text = Initials(displayName ?? colorKey);
        Fill = colorOverride ?? WinoStyle.Hex(ContactPictureAppearance.PaletteColor(colorKey ?? displayName));
        AccessibilityLabel = displayName ?? colorKey;
        if (_image.Image is null) _initials.Hidden = false;
    }

    public UIImage? Image
    {
        get => _image.Image;
        set
        {
            _image.Image = value;
            _image.Hidden = value is null;
            _initials.Hidden = value is not null;
        }
    }

    /// <summary>The shared initials rule (<see cref="ContactPictureAppearance.Initials"/>).</summary>
    public static string Initials(string? name) => ContactPictureAppearance.Initials(name);

    private void UpdateShape() => CornerRadius = _isSquare ? Math.Round(_size * 0.24) : _size / 2;

    public override CGSize IntrinsicContentSize => new(_size, _size);
}
