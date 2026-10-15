using Android.Graphics;

namespace Wino.Presentation.Android;

/// <summary>
/// The surface colours of the active custom theme, per appearance (UIKit and AppKit <c>WinoThemeSurfaces</c>). Views
/// resolve the override when they draw and fall back to the Wino token colour when no custom theme is active. The theme
/// service sets the palette on the UI thread.
/// </summary>
public static class WinoThemeSurfaces
{
    private static Dictionary<WinoThemeSurface, (Color Light, Color Dark)> _surfaces = new();

    /// <summary>Raised on the UI thread after the palette changes.</summary>
    public static event EventHandler? Changed;

    /// <summary>True while a custom theme supplies surface colours.</summary>
    public static bool IsActive => _surfaces.Count > 0;

    /// <summary>Replaces the palette. Call on the UI thread.</summary>
    public static void Apply(IReadOnlyDictionary<WinoThemeSurface, (Color Light, Color Dark)> surfaces)
    {
        _surfaces = new Dictionary<WinoThemeSurface, (Color Light, Color Dark)>(surfaces);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Replaces the palette from platform-neutral colours (the shared theme catalogue).</summary>
    public static void Apply(IReadOnlyDictionary<WinoThemeSurface, (WinoRgba Light, WinoRgba Dark)> surfaces)
    {
        var converted = new Dictionary<WinoThemeSurface, (Color Light, Color Dark)>();
        foreach (var (surface, pair) in surfaces) converted[surface] = (WinoColors.ToColor(pair.Light), WinoColors.ToColor(pair.Dark));
        Apply(converted);
    }

    /// <summary>Removes every override. Call on the UI thread.</summary>
    public static void Clear()
    {
        if (_surfaces.Count == 0) return;
        _surfaces = new();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The override for <paramref name="surface"/> in the given appearance, or null.</summary>
    public static Color? Resolve(WinoThemeSurface surface, bool dark)
        => _surfaces.TryGetValue(surface, out var pair) ? (dark ? pair.Dark : pair.Light) : null;
}
