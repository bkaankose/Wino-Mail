using AppKit;
using CoreGraphics;

namespace Wino.Presentation.AppKit;

/// <summary>
/// A Wino content zone. With a wallpaper or gradient theme it is the rounded, stroked, softly shadowed
/// card that holds the mail list, the reader, a calendar or a settings page, floating over the backdrop
/// like the Windows MailListContainer border. Without one (<see cref="WinoStyle.FloatingZones"/> false)
/// it is a flat pane that runs to its host's edges, the native split-view look.
/// Put content in <see cref="ContentView"/>; the zone clips it to its shape.
/// </summary>
public class WinoZoneView : NSView
{
    private readonly WinoSurfaceView _surface = new();
    private readonly NSShadow _shadow = new() { ShadowBlurRadius = 10, ShadowOffset = new CGSize(0, -2), ShadowColor = NSColor.Black.ColorWithAlphaComponent(0.12f) };
    private double _floatingRadius = WinoStyle.ZoneRadius;

    public WinoZoneView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        _surface.Fill = WinoStyle.ZoneFill;
        WinoLayout.Fill(_surface, this);
        Apply();
        WinoStyle.BackdropWatchers.Add(this, static zone => zone.Apply());
    }

    /// <summary>Add children here; it is clipped to the zone's rounded corners while floating.</summary>
    public NSView ContentView => _surface;

    public NSColor? Fill { get => _surface.Fill; set => _surface.Fill = value; }

    /// <summary>The corner radius while the zone floats (flat zones have none).</summary>
    public double FloatingRadius
    {
        get => _floatingRadius;
        set { _floatingRadius = value; Apply(); }
    }

    /// <summary>
    /// Adds the zone to <paramref name="parent"/> with the given gutters while floating and none while flat.
    /// <paramref name="top"/> anchors to <paramref name="topAnchor"/> when given (a safe-area top).
    /// </summary>
    public void AttachTo(NSView parent, double top, double leading, double bottom, double trailing, NSLayoutYAxisAnchor? topAnchor = null)
    {
        parent.AddSubview(this);
        NSLayoutConstraint.ActivateConstraints(
        [
            WinoZoneInsets.Track(TopAnchor.ConstraintEqualTo(topAnchor ?? parent.TopAnchor), top),
            WinoZoneInsets.Track(LeadingAnchor.ConstraintEqualTo(parent.LeadingAnchor), leading),
            WinoZoneInsets.Track(TrailingAnchor.ConstraintEqualTo(parent.TrailingAnchor), -trailing),
            WinoZoneInsets.Track(BottomAnchor.ConstraintEqualTo(parent.BottomAnchor), -bottom)
        ]);
    }

    private void Apply()
    {
        bool floating = WinoStyle.FloatingZones;
        Shadow = floating ? _shadow : null;
        _surface.CornerRadius = floating ? _floatingRadius : 0;
        _surface.Stroke = floating ? WinoStyle.ZoneStroke : null;
        _surface.NeedsDisplay = true;
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        _surface.NeedsDisplay = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.BackdropWatchers.Remove(this);
        base.Dispose(disposing);
    }
}

/// <summary>
/// Gutter constraints that hold their constant while zones float and collapse to zero while they are flat.
/// Constraints are held weakly, so a disposed view drops out on the next change.
/// </summary>
public static class WinoZoneInsets
{
    private static readonly List<(WeakReference<NSLayoutConstraint> Constraint, double Floating)> _tracked = new();

    static WinoZoneInsets() => WinoStyle.BackdropChanged += (_, _) => Apply();

    /// <summary>Registers <paramref name="constraint"/> with its floating constant and applies the current state.</summary>
    public static NSLayoutConstraint Track(NSLayoutConstraint constraint, double floatingConstant)
    {
        constraint.Constant = (nfloat)(WinoStyle.FloatingZones ? floatingConstant : 0);
        lock (_tracked) _tracked.Add((new WeakReference<NSLayoutConstraint>(constraint), floatingConstant));
        return constraint;
    }

    private static void Apply()
    {
        bool floating = WinoStyle.FloatingZones;
        lock (_tracked)
        {
            _tracked.RemoveAll(entry => !entry.Constraint.TryGetTarget(out var live) || live.Handle == IntPtr.Zero);
            foreach (var (reference, value) in _tracked)
                if (reference.TryGetTarget(out var constraint)) constraint.Constant = (nfloat)(floating ? value : 0);
        }
    }
}
