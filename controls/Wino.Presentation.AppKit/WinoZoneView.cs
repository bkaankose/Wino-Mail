using AppKit;
using CoreGraphics;

namespace Wino.Presentation.AppKit;

/// <summary>
/// A Wino content zone: the rounded, stroked, softly shadowed card that holds the mail list,
/// the reader, a calendar or a settings page, floating over the theme backdrop exactly like
/// the Windows MailListContainer border (8pt radius, 1pt card stroke, theme shadow).
/// Put content in <see cref="ContentView"/>; the zone clips it to the rounded shape.
/// </summary>
public class WinoZoneView : NSView
{
    private readonly WinoSurfaceView _surface = new();

    public WinoZoneView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        Shadow = new NSShadow { ShadowBlurRadius = 10, ShadowOffset = new CGSize(0, -2), ShadowColor = NSColor.Black.ColorWithAlphaComponent(0.12f) };
        _surface.CornerRadius = WinoStyle.ZoneRadius;
        _surface.Fill = WinoStyle.ZoneFill;
        _surface.Stroke = WinoStyle.ZoneStroke;
        WinoLayout.Fill(_surface, this);
    }

    /// <summary>Add children here; it is clipped to the zone's rounded corners.</summary>
    public NSView ContentView => _surface;

    public NSColor? Fill { get => _surface.Fill; set => _surface.Fill = value; }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        _surface.NeedsDisplay = true;
    }
}
