using System;

using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

public class CustomThemeMetadata
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string AccentColorHex { get; set; }
    public bool HasCustomAccentColor => !string.IsNullOrEmpty(AccentColorHex);
    public CustomThemePalette? LightPalette { get; set; }
    public CustomThemePalette? DarkPalette { get; set; }
    public ThemeWallpaperFit WallpaperFit { get; set; } = ThemeWallpaperFit.Fill;
    public ThemeWallpaperAlignment WallpaperAlignment { get; set; } = ThemeWallpaperAlignment.Center;
}
