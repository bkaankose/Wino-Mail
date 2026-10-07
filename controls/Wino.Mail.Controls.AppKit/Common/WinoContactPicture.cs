using AppKit;
using CoreGraphics;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Common;

/// <summary>
/// Circular avatar: a picture when one is set, otherwise initials on a colour from the
/// Wino avatar palette keyed by the address. Sizes used by the design: 40 reader, 32 row, 26 thread child.
/// Set <see cref="IsSquare"/> for account icons (rounded square).
/// </summary>
public sealed class WinoContactPicture : WinoSurfaceView
{
    private readonly NSTextField _initials;
    private readonly NSImageView _image;
    private readonly NSLayoutConstraint _width;
    private readonly NSLayoutConstraint _height;
    private double _size;
    private bool _isSquare;

    public WinoContactPicture(double size = 32)
    {
        _initials = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, NSColor.White);
        _initials.Alignment = NSTextAlignment.Center;
        _image = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
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
        SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ImageRole;
        Size = size;
    }

    public double Size
    {
        get => _size;
        set
        {
            _size = value;
            _width.Constant = _height.Constant = (nfloat)value;
            _initials.Font = NSFont.SystemFontOfSize((nfloat)Math.Max(9, Math.Round(value * 0.38)), NSFontWeight.Semibold);
            UpdateShape();
        }
    }

    public bool IsSquare
    {
        get => _isSquare;
        set { _isSquare = value; UpdateShape(); }
    }

    /// <summary>Shows initials derived from <paramref name="displayName"/> on a colour derived from <paramref name="colorKey"/>.</summary>
    public void SetIdentity(string? displayName, string? colorKey, NSColor? colorOverride = null)
    {
        _initials.StringValue = Initials(displayName ?? colorKey);
        Fill = colorOverride ?? WinoStyle.AvatarColor(colorKey ?? displayName);
        AccessibilityLabel = displayName ?? colorKey;
        if (_image.Image is null) _initials.Hidden = false;
    }

    public NSImage? Image
    {
        get => _image.Image;
        set
        {
            _image.Image = value;
            _image.Hidden = value is null;
            _initials.Hidden = value is not null;
        }
    }

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var cleaned = name.Trim();
        int at = cleaned.IndexOf('@');
        if (at > 0) cleaned = cleaned[..at];
        var parts = cleaned.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return (char.ToUpperInvariant(parts[0][0]).ToString() + char.ToUpperInvariant(parts[^1][0])).ToString();
    }

    private void UpdateShape() => CornerRadius = _isSquare ? Math.Round(_size * 0.24) : _size / 2;

    public override CGSize IntrinsicContentSize => new((nfloat)_size, (nfloat)_size);
}
