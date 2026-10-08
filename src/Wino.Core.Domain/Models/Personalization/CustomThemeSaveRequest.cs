using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

public sealed record CustomThemeSaveRequest(
    Guid? ThemeId,
    string Name,
    string AccentColorHex,
    byte[]? WallpaperData,
    CustomThemePalette? LightPalette,
    CustomThemePalette? DarkPalette,
    ThemeWallpaperFit WallpaperFit,
    ThemeWallpaperAlignment WallpaperAlignment);
