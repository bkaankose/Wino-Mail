using UIKit;

namespace Wino.Presentation.UIKit;

/// <summary>
/// The surface colours of the active custom theme, per appearance (UIKit twin of the AppKit <c>WinoThemeSurfaces</c>).
/// Views take the dynamic colours from here once; they resolve the override when UIKit resolves the colour and fall back
/// to the normal Wino colour when no custom theme is active. The theme service sets the palette on the UI thread.
/// </summary>
public static class WinoThemeSurfaces
{
    private static Dictionary<WinoThemeSurface, (UIColor Light, UIColor Dark)> _surfaces = new();

    /// <summary>Raised on the UI thread after the palette changes.</summary>
    public static event EventHandler? Changed;

    /// <summary>True while a custom theme supplies surface colours.</summary>
    public static bool IsActive => _surfaces.Count > 0;

    /// <summary>Replaces the palette. Call on the UI thread.</summary>
    public static void Apply(IReadOnlyDictionary<WinoThemeSurface, (UIColor Light, UIColor Dark)> surfaces)
    {
        _surfaces = new Dictionary<WinoThemeSurface, (UIColor Light, UIColor Dark)>(surfaces);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Removes every override. Call on the UI thread.</summary>
    public static void Clear()
    {
        if (_surfaces.Count == 0) return;
        _surfaces = new();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The override for <paramref name="surface"/> in the given appearance, or null.</summary>
    public static UIColor? Resolve(WinoThemeSurface surface, bool dark)
        => _surfaces.TryGetValue(surface, out var pair) ? (dark ? pair.Dark : pair.Light) : null;

    /// <summary>A colour that shows the override when one is active and <paramref name="fallback"/> otherwise.</summary>
    public static UIColor Dynamic(WinoThemeSurface surface, UIColor fallback)
        => UIColor.FromDynamicProvider(traits =>
            Resolve(surface, traits.UserInterfaceStyle == UIUserInterfaceStyle.Dark) ?? fallback.GetResolvedColor(traits));

    /// <summary>The reader zone: the ReadingPane override, else the normal zone fill.</summary>
    public static UIColor ReadingPaneFill => Dynamic(WinoThemeSurface.ReadingPane, WinoStyle.ZoneFill);

    /// <summary>The sidebar tint: the Navigation override, else clear.</summary>
    public static UIColor NavigationFill => Dynamic(WinoThemeSurface.Navigation, UIColor.Clear);
}
