using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Personalization;

/// <summary>
/// A starting point for the base surface, so a new theme does not begin as an empty form.
/// </summary>
/// <param name="Name">Localized display name.</param>
/// <param name="LightColor">Base surface for the Light palette, as #AARRGGBB.</param>
/// <param name="DarkColor">Base surface for the Dark palette, as #AARRGGBB.</param>
public sealed record ThemeBasePreset(string Name, string LightColor, string DarkColor);

public static class ThemeBasePresets
{
    /// <summary>
    /// Builds the preset list. Names are resolved on every call because the active
    /// translation is not available when this type is first loaded.
    /// </summary>
    public static IReadOnlyList<ThemeBasePreset> Create() => (ThemeBasePreset[])
    [
        new(Translator.ApplicationThemeEditor_PresetSlate, "#D9F2F3F5", "#E61F2430"),
        new(Translator.ApplicationThemeEditor_PresetGraphite, "#D9EDEDED", "#E6141414"),
        new(Translator.ApplicationThemeEditor_PresetTeal, "#D9E7F2F1", "#E612262B"),
        new(Translator.ApplicationThemeEditor_PresetPlum, "#D9F3EEF5", "#E6241A2C"),
        new(Translator.ApplicationThemeEditor_PresetSand, "#D9F4EFE6", "#E62A241B")
    ];
}
