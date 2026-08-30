using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace Wino.Mail.Controls.AppModeSwitcher;

/// <summary>
/// Recolours the switcher artwork at runtime.
///
/// SVG assets cannot reference theme resources: Direct2D rasterises them from a file, long
/// after XAML has resolved anything. So the four app glyphs are authored once in the real
/// Wino blues - which keeps them correct icons in any SVG viewer - and every colour that has
/// to move is drawn from the small palette below. This class substitutes that palette before
/// handing the markup to <see cref="SvgImageSource"/>.
///
/// Two inputs drive it. The accent gives the blues, so the glyphs follow whatever accent the
/// user has set. The paper colour gives the white regions, so a glyph can sit on a light or a
/// dark surface without needing a second, hand-recoloured copy of the artwork.
/// </summary>
internal static partial class AppModeGlyphPalette
{
    /// <summary>
    /// The authored colours, and what each one means. Anything outside this map - the drop
    /// shadow colours, for instance - is left exactly as authored.
    /// </summary>
    private const string AccentLight3Token = "#20DFFF";
    private const string AccentLight2Token = "#00C4FF";
    private const string AccentLight1Token = "#00AAFF";
    private const string AccentToken = "#0090F7";
    private const string AccentDark1Token = "#005FE4";
    private const string AccentSoftToken = "#CFE1FB";
    private const string PaperToken = "#FFFFFF";
    private const string PaperShadeToken = "#F1F5FB";
    private const string PaperDimToken = "#CDD0D5";

    /// <summary>
    /// Used when nothing has published <c>SystemAccentColor</c> yet, which happens in the
    /// designer and in the first moments of a cold start.
    /// </summary>
    private static readonly Color FallbackAccent = Color.FromArgb(255, 0x00, 0x78, 0xD4);

    // The chip and the selection tile are the same accent tint at different lightnesses:
    // one surface family, two depths. Pinning each to a lightness is what keeps them weighing
    // the same whatever the accent is - mixing alone cannot, because a dark blue accent lands
    // far heavier than an orange one - so the accent only ever decides hue.

    /// <summary>
    /// Light enough to stay a rest state, dark enough to separate white artwork from the card.
    /// It takes very little: the artwork's white regions are bounded by its accent-coloured
    /// ones, so the chip only has to break the white-on-white edge rather than carry the
    /// separation on its own. Anything heavier stops reading as rest.
    /// </summary>
    private const double ChipLightness = 210d;

    /// <summary>
    /// The Light tile. Deep enough to read as selected against a white card, and to give the
    /// artwork's white regions something to sit on, without going to a flat black that has
    /// nothing to do with the chip it replaces.
    /// </summary>
    private const double LightSelectionLightness = 64d;

    /// <summary>
    /// The Dark tile. A flat neutral, lifted off the card rather than inverted against it:
    /// the glyphs keep their white regions in both themes, and a near-white tile would
    /// swallow them. It is not accent tinted, because Dark has no resting chip for it to be
    /// continuous with - it is the only fill in the strip, and a neutral keeps the artwork
    /// the only coloured thing there.
    /// </summary>
    private static readonly Color DarkSelection = Color.FromArgb(255, 0x4D, 0x4D, 0x4D);

    private static readonly Dictionary<string, SvgImageSource> _glyphCache = [];
    private static readonly Dictionary<string, string> _markupCache = [];

    [GeneratedRegex("#[0-9A-Fa-f]{6}")]
    private static partial Regex ColorPattern();

    /// <summary>
    /// The accent the app is currently using. <c>SystemAccentColor</c> is mutated in place
    /// when the user picks a different accent, so reading it is enough to stay current; the
    /// host tells the control when to look again.
    /// </summary>
    public static Color ResolveAccent()
    {
        if (Application.Current?.Resources.TryGetValue("SystemAccentColor", out var value) == true
            && value is Color accent)
        {
            return accent;
        }

        return FallbackAccent;
    }

    /// <summary>
    /// The fill behind an unselected glyph. Light needs one so the white regions in the
    /// artwork stop dissolving into the card; Dark does not, because the card is already
    /// darker than the artwork.
    /// </summary>
    public static Color ResolveRestingChip(Color accent, ElementTheme theme)
    {
        if (theme == ElementTheme.Dark)
            return Colors.Transparent;

        return Tint(accent, ChipLightness);
    }

    /// <summary>
    /// The fill behind the selected glyph. In Light it is the resting chip taken darker rather
    /// than a separate colour, so selecting a mode reads as that mode's chip deepening.
    /// </summary>
    public static Color ResolveSelectionTile(Color accent, ElementTheme theme)
        => theme == ElementTheme.Dark ? DarkSelection : Tint(accent, LightSelectionLightness);

    /// <summary>
    /// Pulls the accent most of the way to a neutral, then pins the result to a lightness.
    /// </summary>
    private static Color Tint(Color accent, double lightness)
        => Normalize(Blend(accent, Color.FromArgb(255, 0xC9, 0xC9, 0xC9), 0.7), lightness);

    /// <summary>
    /// The colour the artwork's white regions take.
    ///
    /// It never changes. Every surface a glyph can land on - the grey chip, the near-black
    /// tile in Light, the card and the lifted tile in Dark - is darker than the artwork, so
    /// white always reads. That is a constraint on the surfaces rather than a coincidence:
    /// darkening the paper to survive a light tile turns the app icons into flat silhouettes,
    /// which costs far more than the tile gains.
    /// </summary>
    public static Color Paper => Colors.White;

    /// <summary>
    /// Builds the recoloured artwork at a given size, in device pixels.
    ///
    /// The size is not optional. An <see cref="SvgImageSource"/> given a URI re-rasterises
    /// itself whenever its layout size or the display scale changes, because it can always go
    /// back and decode the file again. One given a stream cannot: the stream is consumed on
    /// load, so whatever it rasterises to on that single pass is what it stays, and the
    /// default pass has no idea how small the glyph will be drawn. Asking for the exact pixel
    /// size is what keeps the artwork sharp instead of leaving a bitmap to be resampled.
    ///
    /// Results are cached by asset, accent, paper and size, so the repeated calls that
    /// selection and theme changes cause cost nothing after the first.
    /// </summary>
    public static async Task<SvgImageSource?> CreateGlyphAsync(Uri source, Color accent, Color paper, int pixelSize)
    {
        var key = $"{source}|{accent}|{paper}|{pixelSize}";

        if (_glyphCache.TryGetValue(key, out var cached))
            return cached;

        var markup = await LoadMarkupAsync(source).ConfigureAwait(true);

        if (markup is null)
            return null;

        var recoloured = Substitute(markup, accent, paper);

        var image = new SvgImageSource
        {
            RasterizePixelWidth = pixelSize,
            RasterizePixelHeight = pixelSize
        };

        using (var stream = new InMemoryRandomAccessStream())
        {
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(Encoding.UTF8.GetBytes(recoloured));
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            var status = await image.SetSourceAsync(stream);

            if (status != SvgImageSourceLoadStatus.Success)
                return null;
        }

        // A glyph is small and the set is fixed, so the cache is left to grow: it tops out at
        // the number of assets, per accent the user tries and per display scale they use.
        _glyphCache[key] = image;

        return image;
    }

    private static async Task<string?> LoadMarkupAsync(Uri source)
    {
        var key = source.ToString();

        if (_markupCache.TryGetValue(key, out var cached))
            return cached;

        try
        {
            var file = await StorageFile.GetFileFromApplicationUriAsync(source);
            var markup = await FileIO.ReadTextAsync(file);

            _markupCache[key] = markup;

            return markup;
        }
        catch (Exception)
        {
            // A missing or unreadable asset leaves the item without a glyph rather than
            // taking the shell down with it.
            return null;
        }
    }

    /// <summary>
    /// One pass over the markup, so a substituted colour can never be substituted again by a
    /// later token that happens to match what the first one produced.
    /// </summary>
    private static string Substitute(string markup, Color accent, Color paper)
    {
        var white = Colors.White;
        var black = Colors.Black;
        var inverse = IsLight(paper) ? black : white;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AccentLight3Token] = ToHex(Blend(accent, white, 0.60)),
            [AccentLight2Token] = ToHex(Blend(accent, white, 0.40)),
            [AccentLight1Token] = ToHex(Blend(accent, white, 0.20)),
            [AccentToken] = ToHex(accent),
            [AccentDark1Token] = ToHex(Blend(accent, black, 0.20)),
            [AccentSoftToken] = ToHex(Blend(accent, paper, 0.72)),
            [PaperToken] = ToHex(paper),
            [PaperShadeToken] = ToHex(Blend(paper, inverse, 0.04)),
            [PaperDimToken] = ToHex(Blend(paper, inverse, 0.18))
        };

        return ColorPattern().Replace(markup, match => map.TryGetValue(match.Value, out var replacement)
            ? replacement
            : match.Value);
    }

    private static Color Blend(Color from, Color to, double amount)
        => Color.FromArgb(
            255,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    /// <summary>
    /// Lifts or drops a colour until it reaches a target lightness, keeping its hue.
    /// </summary>
    private static Color Normalize(Color color, double target)
    {
        var lightness = Lightness(color);

        if (lightness <= 0d)
            return Blend(Colors.Black, Colors.White, target / 255d);

        return lightness < target
            ? Blend(color, Colors.White, (target - lightness) / (255d - lightness))
            : Blend(color, Colors.Black, 1d - (target / lightness));
    }

    private static double Lightness(Color color)
        => (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);

    private static bool IsLight(Color color) => Lightness(color) >= 128d;

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
