using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

public static class CustomThemeSaveValidator
{
    public static CustomThemeValidationError Validate(
        CustomThemeSaveRequest request,
        IEnumerable<CustomThemeMetadata> existingThemes)
    {
        var themes = existingThemes.ToList();
        var name = request.Name?.Trim();

        if (string.IsNullOrEmpty(name))
            return CustomThemeValidationError.MissingName;

        var existingTheme = request.ThemeId.HasValue
            ? themes.FirstOrDefault(theme => theme.Id == request.ThemeId.Value)
            : null;

        if (request.ThemeId.HasValue && existingTheme == null)
            return CustomThemeValidationError.MissingTheme;

        if (themes.Any(theme => theme.Id != request.ThemeId && string.Equals(theme.Name.Trim(), name, System.StringComparison.OrdinalIgnoreCase)))
            return CustomThemeValidationError.DuplicateName;

        if (existingTheme == null && (request.WallpaperData == null || request.WallpaperData.Length == 0))
            return CustomThemeValidationError.MissingWallpaper;

        if (!string.IsNullOrWhiteSpace(request.AccentColorHex) && !ThemeColorValidator.TryNormalizeOpaque(request.AccentColorHex, out _))
            return CustomThemeValidationError.InvalidAccent;

        return ThemeColorValidator.IsValid(request.LightPalette) && ThemeColorValidator.IsValid(request.DarkPalette)
            ? CustomThemeValidationError.None
            : CustomThemeValidationError.InvalidSurface;
    }
}
