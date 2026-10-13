namespace Wino.Mail.Controls.Core;

/// <summary>
/// Platform-neutral rules for initials avatars on the native heads: the 16-step Wino avatar palette,
/// a stable colour per address and the initials text. Platforms turn the palette entry into their own
/// colour type.
/// </summary>
public static class ContactPictureAppearance
{
    /// <summary>The Wino avatar palette as 0xRRGGBB values.</summary>
    public static IReadOnlyList<uint> Palette { get; } = (uint[])
    [
        0xE74C3C, 0xC0392B, 0x9B59B6, 0x8E44AD,
        0x3498DB, 0x2980B9, 0x16A085, 0x27AE60,
        0x00796B, 0xE67E22, 0xD35400, 0x663399,
        0x3F51B5, 0xE91E63, 0x7F8C8D, 0x795548
    ];

    /// <summary>The neutral grey used when there is no key.</summary>
    public const int FallbackPaletteIndex = 14;

    /// <summary>
    /// A stable palette index for <paramref name="key"/> (case-insensitive 31-multiplier string hash, so the
    /// same address keeps its colour across launches and platforms).
    /// </summary>
    public static int PaletteIndex(string? key)
    {
        if (string.IsNullOrEmpty(key)) return FallbackPaletteIndex;

        int hash = 17;
        foreach (char character in key.ToLowerInvariant()) hash = unchecked(hash * 31 + character);
        return (int)((uint)hash % (uint)Palette.Count);
    }

    /// <summary>The palette colour for <paramref name="key"/> as 0xRRGGBB.</summary>
    public static uint PaletteColor(string? key) => Palette[PaletteIndex(key)];

    /// <summary>The palette colour for <paramref name="key"/> as #RRGGBB.</summary>
    public static string PaletteColorHex(string? key) => $"#{PaletteColor(key):X6}";

    /// <summary>
    /// Up to two initials: the first letters of the first and last name parts, or the first two letters of a
    /// single part. An address uses its local part. "?" when nothing usable remains.
    /// </summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";

        var cleaned = name.Trim();
        int at = cleaned.IndexOf('@');
        if (at > 0) cleaned = cleaned[..at];

        var parts = cleaned.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();

        return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}";
    }
}
