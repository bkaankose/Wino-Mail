using AppKit;

namespace Wino.Presentation.AppKit;

/// <summary>A surface a custom Wino theme can recolour (the Windows Custom.xaml palette resources).</summary>
public enum WinoThemeSurface
{
    /// <summary>WinoContentZoneBackgroud: every floating zone.</summary>
    Workspace,
    /// <summary>ReadingPaneBackgroundColorBrush: the reader zone.</summary>
    ReadingPane,
    /// <summary>MailListHeaderBackgroundColor: the mail list date group strip.</summary>
    MailListHeader,
    /// <summary>NavigationViewContentBackground: a tint over the sidebar.</summary>
    Navigation,
    /// <summary>CalendarDefaultHourBackgroundBrush: calendar slots outside working hours.</summary>
    CalendarDefaultHour,
    /// <summary>CalendarWorkHourBackgroundBrush: calendar slots inside working hours.</summary>
    CalendarWorkHour
}

/// <summary>
/// The surface colours of the active custom theme, per appearance. Views take the dynamic colours
/// from here once; they resolve the override at draw time and fall back to the normal Wino colour
/// when no custom theme is active, so nothing changes for predefined themes. The theme service sets
/// the palette on the UI thread and redraws the windows afterwards.
/// </summary>
public static class WinoThemeSurfaces
{
    private static Dictionary<WinoThemeSurface, (NSColor Light, NSColor Dark)> _surfaces = new();

    /// <summary>Raised on the UI thread after the palette changes.</summary>
    public static event EventHandler? Changed;

    /// <summary>True while a custom theme supplies surface colours.</summary>
    public static bool IsActive => _surfaces.Count > 0;

    /// <summary>Replaces the palette. Call on the UI thread.</summary>
    public static void Apply(IReadOnlyDictionary<WinoThemeSurface, (NSColor Light, NSColor Dark)> surfaces)
    {
        _surfaces = new Dictionary<WinoThemeSurface, (NSColor Light, NSColor Dark)>(surfaces);
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
    public static NSColor? Resolve(WinoThemeSurface surface, bool dark)
        => _surfaces.TryGetValue(surface, out var pair) ? (dark ? pair.Dark : pair.Light) : null;

    /// <summary>A colour that shows the override when one is active and <paramref name="fallback"/> otherwise.</summary>
    public static NSColor Dynamic(WinoThemeSurface surface, NSColor fallback)
        => NSColor.GetColor(string.Empty, appearance =>
            Resolve(surface, appearance.FindBestMatch([NSAppearance.NameAqua, NSAppearance.NameDarkAqua]) == NSAppearance.NameDarkAqua) ?? fallback);

    /// <summary>The reader zone: the ReadingPane override, else the normal zone fill.</summary>
    public static NSColor ReadingPaneFill => Dynamic(WinoThemeSurface.ReadingPane, WinoStyle.ZoneFill);

    /// <summary>The sidebar tint: the Navigation override, else clear.</summary>
    public static NSColor NavigationFill => Dynamic(WinoThemeSurface.Navigation, NSColor.Clear);
}
