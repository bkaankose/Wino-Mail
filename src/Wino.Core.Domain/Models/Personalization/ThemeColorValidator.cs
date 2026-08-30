using System;

namespace Wino.Core.Domain.Models.Personalization;

public static class ThemeColorValidator
{
    public static bool TryNormalizeOpaque(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 8)
            hex = hex[2..];

        if (hex.Length != 6 || !IsHex(hex))
            return false;

        normalized = $"#{hex.ToUpperInvariant()}";
        return true;
    }

    public static bool IsValidSurface(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var hex = value.Trim().TrimStart('#');
        return hex.Length is 6 or 8 && IsHex(hex);
    }

    public static bool IsValid(CustomThemePalette? palette)
    {
        if (palette == null)
            return true;

        foreach (Enums.CustomThemeColorKey key in Enum.GetValues<Enums.CustomThemeColorKey>())
        {
            if (!IsValidSurface(palette.GetOverride(key)))
                return false;
        }

        return true;
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }

        return true;
    }
}
