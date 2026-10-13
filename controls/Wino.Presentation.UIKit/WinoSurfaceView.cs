using CoreAnimation;
using UIKit;

namespace Wino.Presentation.UIKit;

/// <summary>
/// A view with a fill, stroke and corner radius (UIKit twin of the AppKit <c>WinoSurfaceView</c>). The fill is the
/// view's background colour, so dynamic colours follow light and dark by themselves; the stroke is a layer border
/// that is re-resolved after every appearance change.
/// </summary>
public class WinoSurfaceView : UIView
{
    public const CACornerMask AllCorners = CACornerMask.MinXMinYCorner | CACornerMask.MaxXMinYCorner | CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner;

    private UIColor? _fill;
    private UIColor? _stroke;
    private double _cornerRadius;
    private double _strokeWidth = 1;
    private CACornerMask _corners = AllCorners;

    public WinoSurfaceView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Layer.CornerCurve = CACornerCurve.Continuous;
        WinoTraits.OnAppearanceChanged(this, static view => view.ApplySurface());
        ApplySurface();
    }

    public UIColor? Fill { get => _fill; set { _fill = value; ApplySurface(); } }
    public UIColor? Stroke { get => _stroke; set { _stroke = value; ApplySurface(); } }
    public double StrokeWidth { get => _strokeWidth; set { _strokeWidth = value; ApplySurface(); } }
    public double CornerRadius { get => _cornerRadius; set { _cornerRadius = value; ApplySurface(); } }
    public CACornerMask Corners { get => _corners; set { _corners = value; ApplySurface(); } }

    /// <summary>Re-applies the colours, for subclasses that draw and after an appearance change.</summary>
    protected virtual void ApplySurface()
    {
        BackgroundColor = _fill ?? UIColor.Clear;
        if (_stroke is { } stroke)
        {
            Layer.BorderColor = stroke.GetResolvedColor(TraitCollection).CGColor;
            Layer.BorderWidth = (nfloat)_strokeWidth;
        }
        else
        {
            Layer.BorderWidth = 0;
        }
        Layer.CornerRadius = (nfloat)_cornerRadius;
        Layer.MaskedCorners = _corners;
        ClipsToBounds = _cornerRadius > 0;
    }
}

/// <summary>A hairline horizontal or vertical rule in the separator colour.</summary>
public sealed class WinoSeparator : WinoSurfaceView
{
    private readonly NSLayoutConstraint _thickness;

    public WinoSeparator(bool vertical = false, double leadingInset = 0)
    {
        Fill = WinoStyle.Separator;
        _thickness = vertical ? WidthAnchor.ConstraintEqualTo(1) : HeightAnchor.ConstraintEqualTo(1);
        _thickness.Active = true;
        LeadingInset = leadingInset;
        RegisterForTraitChanges(new[] { typeof(UITraitDisplayScale) }, static (environment, _) =>
        {
            if (environment is WinoSeparator separator) separator.UpdateThickness();
        });
        UpdateThickness();
    }

    /// <summary>Indent the owner applies when pinning this separator, used to align with row text.</summary>
    public double LeadingInset { get; }

    private void UpdateThickness()
    {
        var scale = TraitCollection.DisplayScale;
        _thickness.Constant = scale > 0 ? 1 / scale : 1;
    }
}

/// <summary>
/// A Wino content zone (UIKit twin of the AppKit <c>WinoZoneView</c>): the rounded, stroked, softly shadowed card that
/// holds a list, the reader or a settings page and floats over the theme backdrop. Put content in
/// <see cref="ContentView"/>; the zone clips it to the rounded shape while the shadow stays outside.
/// </summary>
public class WinoZoneView : UIView
{
    private readonly WinoSurfaceView _surface = new();

    public WinoZoneView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Layer.ShadowColor = UIColor.Black.CGColor;
        Layer.ShadowOpacity = 0.12f;
        Layer.ShadowRadius = 5;
        Layer.ShadowOffset = new CoreGraphics.CGSize(0, 2);
        _surface.CornerRadius = WinoStyle.ZoneRadius;
        _surface.Fill = WinoStyle.ZoneFill;
        _surface.Stroke = WinoStyle.ZoneStroke;
        WinoLayout.Fill(_surface, this);
    }

    /// <summary>Add children here; it is clipped to the zone's rounded corners.</summary>
    public UIView ContentView => _surface;

    public UIColor? Fill { get => _surface.Fill; set => _surface.Fill = value; }

    public double CornerRadius { get => _surface.CornerRadius; set => _surface.CornerRadius = value; }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        // A shadow path keeps scrolling cheap: the layer does not have to derive the shape from its content.
        Layer.ShadowPath = UIBezierPath.FromRoundedRect(Bounds, (nfloat)_surface.CornerRadius).CGPath;
    }
}
