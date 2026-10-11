using System.Text.RegularExpressions;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;

namespace Wino.Services.Extensions;

/// <summary>
/// Projects the HTML that the reader renders. Rendered HTML embeds inline (cid:) images as base64
/// data URLs, which carry no text but can push a short message past the projector's input limit.
/// </summary>
public static partial class MailContentProjectorExtensions
{
    /// <summary>The projector's own default limit, applied to the HTML without embedded image payloads.</summary>
    public const int MaxTextInputCharacters = 1_000_000;

    /// <summary>Returns the input limit for <paramref name="html"/>: the text limit plus its embedded image data.</summary>
    public static int GetRenderedHtmlInputLimit(string html)
    {
        long embeddedImageCharacters = 0;

        foreach (var match in EmbeddedImageDataUrlRegex().EnumerateMatches(html ?? string.Empty))
            embeddedImageCharacters += match.Length;

        return (int)System.Math.Min(int.MaxValue, MaxTextInputCharacters + embeddedImageCharacters);
    }

    /// <summary>Whether <paramref name="html"/> fits the projector once embedded image data is excluded.</summary>
    public static bool CanProjectRenderedHtml(string html)
        => (html ?? string.Empty).Length <= GetRenderedHtmlInputLimit(html);

    /// <summary>
    /// Projects rendered HTML without counting embedded image data toward the input limit.
    /// Throws like <see cref="IMailContentProjector.Project"/> when the remaining HTML is still too large.
    /// </summary>
    public static MailContentProjectionResult ProjectRenderedHtml(
        this IMailContentProjector projector,
        string html,
        MailContentProjectionProfile profile)
        => projector.Project(html, profile, GetRenderedHtmlInputLimit(html));

    /// <summary>
    /// Removes embedded image data URLs from <paramref name="html"/> before it leaves the device.
    /// Wino Intelligence works on text only; the API drops images, so their data is never needed.
    /// </summary>
    public static string RemoveEmbeddedImageData(string html)
        => string.IsNullOrEmpty(html) ? html ?? string.Empty : EmbeddedImageDataUrlRegex().Replace(html, string.Empty);

    [GeneratedRegex(@"data:image/[a-z0-9.+-]+;base64,[a-z0-9+/=]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedImageDataUrlRegex();
}
