using Microsoft.UI.Xaml;

namespace Wino.Mail.Controls.IntelligenceTileBar;

/// <summary>
/// x:Bind helpers for tile templates. A tile carries a glyph only when its kind has a recognizable
/// icon; the rest fall back to the neutral intelligence dot, and these decide which one shows.
/// </summary>
public static class IntelligenceTileVisuals
{
    public static Visibility GlyphVisibility(string? glyph)
        => string.IsNullOrWhiteSpace(glyph) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility DotVisibility(string? glyph)
        => string.IsNullOrWhiteSpace(glyph) ? Visibility.Visible : Visibility.Collapsed;

}
