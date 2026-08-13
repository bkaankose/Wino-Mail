using Windows.UI;

namespace Wino.Mail.Controls.ContactPicture;

/// <summary>
/// Pure helpers for the initials fallback visual of <see cref="WinoContactPicture"/>.
/// </summary>
internal static class ContactVisual
{
    // Flat UI mid-tones curated to stay clearly visible on both light and dark
    // app backgrounds while carrying white initials text.
    private static readonly Color[] Palette =
    [
        FromRgb(0xE7, 0x4C, 0x3C), // Alizarin
        FromRgb(0xC0, 0x39, 0x2B), // Pomegranate
        FromRgb(0x9B, 0x59, 0xB6), // Amethyst
        FromRgb(0x8E, 0x44, 0xAD), // Wisteria
        FromRgb(0x34, 0x98, 0xDB), // Peter River
        FromRgb(0x29, 0x80, 0xB9), // Belize Hole
        FromRgb(0x16, 0xA0, 0x85), // Green Sea
        FromRgb(0x27, 0xAE, 0x60), // Nephritis
        FromRgb(0x00, 0x79, 0x6B), // Teal
        FromRgb(0xE6, 0x7E, 0x22), // Carrot
        FromRgb(0xD3, 0x54, 0x00), // Pumpkin
        FromRgb(0x66, 0x33, 0x99), // Rebecca Purple
        FromRgb(0x3F, 0x51, 0xB5), // Indigo
        FromRgb(0xE9, 0x1E, 0x63), // Pink
        FromRgb(0x7F, 0x8C, 0x8D), // Asbestos
        FromRgb(0x79, 0x55, 0x48), // Brown
    ];

    /// <summary>
    /// First letters of the first two words of the name, uppercased.
    /// Falls back to the first character of the address, then "?".
    /// </summary>
    public static string GetInitials(string? name, string? address)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length >= 2)
            {
                return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
            }

            if (words.Length == 1)
            {
                return char.ToUpperInvariant(words[0][0]).ToString();
            }
        }

        var trimmedAddress = address?.Trim();
        if (!string.IsNullOrEmpty(trimmedAddress))
        {
            return char.ToUpperInvariant(trimmedAddress[0]).ToString();
        }

        return "?";
    }

    /// <summary>
    /// Deterministic flat UI background color for the given identity key.
    /// The same key always yields the same color across sessions.
    /// </summary>
    public static Color GetBackgroundColor(string key)
        => Palette[Fnv1aHash(key.ToLowerInvariant()) % (uint)Palette.Length];

    // FNV-1a instead of string.GetHashCode(), which is randomized per process
    // and would change avatar colors on every launch.
    private static uint Fnv1aHash(string value)
    {
        var hash = 2166136261u;

        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619u;
        }

        return hash;
    }

    private static Color FromRgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}
