using AppKit;
using CoreAnimation;

namespace Wino.Presentation.AppKit;

/// <summary>
/// Layer-backed view with a fill, stroke and corner radius. Colours are re-resolved in
/// <see cref="UpdateLayer"/>, which AppKit calls with the effective appearance current, so
/// semantic and dynamic colours follow light/dark switches without extra observers.
/// </summary>
public class WinoSurfaceView : NSView
{
    private NSColor? _fill;
    private NSColor? _stroke;
    private double _cornerRadius;
    private double _strokeWidth = 1;
    private CACornerMask _corners = CACornerMask.MinXMinYCorner | CACornerMask.MaxXMinYCorner | CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner;

    public WinoSurfaceView()
    {
        WantsLayer = true;
        TranslatesAutoresizingMaskIntoConstraints = false;
        LayerContentsRedrawPolicy = NSViewLayerContentsRedrawPolicy.OnSetNeedsDisplay;
    }

    public NSColor? Fill { get => _fill; set { _fill = value; NeedsDisplay = true; } }
    public NSColor? Stroke { get => _stroke; set { _stroke = value; NeedsDisplay = true; } }
    public double StrokeWidth { get => _strokeWidth; set { _strokeWidth = value; NeedsDisplay = true; } }
    public double CornerRadius { get => _cornerRadius; set { _cornerRadius = value; NeedsDisplay = true; } }
    public CACornerMask Corners { get => _corners; set { _corners = value; NeedsDisplay = true; } }

    public override bool WantsUpdateLayer => true;
    public override bool IsFlipped => true;

    public override void UpdateLayer()
    {
        if (Layer is not { } layer) return;
        layer.BackgroundColor = _fill?.CGColor;
        layer.BorderColor = _stroke?.CGColor;
        layer.BorderWidth = _stroke is null ? 0 : (nfloat)_strokeWidth;
        layer.CornerRadius = (nfloat)_cornerRadius;
        layer.MaskedCorners = _corners;
        layer.MasksToBounds = _cornerRadius > 0;
    }
}

/// <summary>A one-pixel horizontal or vertical rule in the separator colour.</summary>
public sealed class WinoSeparator : WinoSurfaceView
{
    public WinoSeparator(bool vertical = false, double leadingInset = 0)
    {
        Fill = WinoStyle.Separator;
        if (vertical) WidthAnchor.ConstraintEqualTo(1).Active = true;
        else HeightAnchor.ConstraintEqualTo(1).Active = true;
        LeadingInset = leadingInset;
    }

    /// <summary>Indent the owner applies when pinning this separator, used to align with row text.</summary>
    public double LeadingInset { get; }
}
