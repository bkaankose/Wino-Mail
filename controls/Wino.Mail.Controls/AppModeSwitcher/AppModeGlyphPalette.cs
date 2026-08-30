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
/// after XAML has resolved anything. Two things follow from that, and they are handled in
/// different places. Which surface a glyph is drawn for is decided by the theme dictionary
/// in Themes/WinoAppIcons.xaml, which simply names a different asset per theme. Which accent
/// it is drawn in is decided here: the glyphs are authored once in the real Wino blues -
/// which keeps them correct icons in any SVG viewer - and this class substitutes the app
/// accent into them before handing the markup to <see cref="SvgImageSource"/>.
///
/// Nothing here knows about themes. The paper regions in the artwork are whatever the asset
/// says they are.
/// </summary>
internal static partial class AppModeGlyphPalette
{
    /// <summary>
    /// The authored colours, and what each one means. Anything outside this map - the paper
    /// colours and the drop shadow colours - is left exactly as authored.
    /// </summary>
    private const string AccentLight3Token = "#20DFFF";
    private const string AccentLight2Token = "#00C4FF";
    private const string AccentLight1Token = "#00AAFF";
    private const string AccentToken = "#0090F7";
    private const string AccentDark1Token = "#005FE4";
    private const string AccentSoftToken = "#CFE1FB";

    /// <summary>
    /// The single token the monochrome artwork is authored in. Those assets carry no second
    /// colour at all: their light regions are either holes or the same ink at a lower alpha,
    /// so one substitution paints the whole glyph.
    /// </summary>
    private const string InkToken = "#101010";

    /// <summary>
    /// Used when nothing has published <c>SystemAccentColor</c> yet, which happens in the
    /// designer and in the first moments of a cold start.
    /// </summary>
    private static readonly Color FallbackAccent = Color.FromArgb(255, 0x00, 0x78, 0xD4);

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
    /// The fill behind the selected glyph in monochrome. It is the accent itself at a low
    /// alpha rather than a tint of it: the monochrome glyph is already the accent, so the cell
    /// only has to say which one it is, not carry the contrast the glyph is carrying. Alpha
    /// rather than a mixed colour because whatever is behind the strip shows through the
    /// glyph's own holes, and the two have to agree.
    ///
    /// The coloured strip needs no equivalent. Its tile is a plain theme brush, because the
    /// glyph resting on it was already drawn for a surface of that theme.
    /// </summary>
    public static Color ResolveSelectionGlow(Color accent, ElementTheme theme)
        => Color.FromArgb(theme == ElementTheme.Dark ? (byte)0x33 : (byte)0x26, accent.R, accent.G, accent.B);

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
    /// Results are cached by asset, accent and size, so the repeated calls that selection and
    /// theme changes cause cost nothing after the first.
    /// </summary>
    public static Task<SvgImageSource?> CreateGlyphAsync(Uri source, Color accent, int pixelSize)
        => CreateAsync(
            source,
            $"{source}|{accent}|{pixelSize}",
            markup => Substitute(markup, accent),
            pixelSize);

    /// <summary>
    /// Builds a monochrome glyph in a single ink.
    ///
    /// There is no paper colour, and that is the point. A second colour would have to be the
    /// colour of whatever the glyph is resting on, and in monochrome the cell behind it moves:
    /// a hover wash, the selection glow. The monochrome assets carry their light regions as
    /// holes and as alpha instead, so the surface shows through whatever it happens to be.
    /// </summary>
    public static Task<SvgImageSource?> CreateMonochromeGlyphAsync(Uri source, Color ink, int pixelSize)
        => CreateAsync(
            source,
            $"{source}|ink|{ink}|{pixelSize}",
            markup => markup.Replace(InkToken, ToHex(ink), StringComparison.OrdinalIgnoreCase),
            pixelSize);

    private static async Task<SvgImageSource?> CreateAsync(Uri source, string key, Func<string, string> recolour, int pixelSize)
    {
        if (_glyphCache.TryGetValue(key, out var cached))
            return cached;

        var markup = await LoadMarkupAsync(source).ConfigureAwait(true);

        if (markup is null)
            return null;

        var recoloured = recolour(markup);

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
    private static string Substitute(string markup, Color accent)
    {
        var white = Colors.White;
        var black = Colors.Black;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AccentLight3Token] = ToHex(Blend(accent, white, 0.60)),
            [AccentLight2Token] = ToHex(Blend(accent, white, 0.40)),
            [AccentLight1Token] = ToHex(Blend(accent, white, 0.20)),
            [AccentToken] = ToHex(accent),
            [AccentDark1Token] = ToHex(Blend(accent, black, 0.20)),
            [AccentSoftToken] = ToHex(Blend(accent, white, 0.72))
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

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
