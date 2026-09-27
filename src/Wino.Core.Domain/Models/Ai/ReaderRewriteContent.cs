#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Models.Ai;

/// <summary>
/// Shapes a received message for the rewrite endpoint and turns its answer back into text.
/// </summary>
/// <remarks>
/// The reader does not send the rendered HTML: newsletters and long threads routinely exceed the
/// API's <c>AiEmail:RewriteMaxInputChars</c> limit, and their layout markup is noise to the model.
/// It sends the inference projection instead, one paragraph per segment, limited to the current
/// message whenever the projection can tell it apart from quoted history.
/// </remarks>
public static partial class ReaderRewriteContent
{
    /// <summary>
    /// The request budget. It stays below the API's 120,000 character default so the escaped
    /// output is never rejected as too large.
    /// </summary>
    public const int DefaultMaximumHtmlLength = 100_000;

    /// <summary>
    /// The API's default <c>AiEmail:RewriteMaxInputChars</c>. Drafts are checked against it before
    /// sending, so an oversized draft gets a clear error without spending a request.
    /// </summary>
    public const int ApiMaximumHtmlLength = 120_000;

    public static string BuildRequestHtml(IReadOnlyList<MailContentSegment>? segments, int maximumHtmlLength = DefaultMaximumHtmlLength)
    {
        if (segments is null || segments.Count == 0 || maximumHtmlLength <= 0)
            return string.Empty;

        var hasCurrentMessage = false;
        foreach (var segment in segments)
        {
            if (segment.Section == MailContentSection.CurrentMessage && !string.IsNullOrWhiteSpace(segment.Text))
            {
                hasCurrentMessage = true;
                break;
            }
        }

        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (hasCurrentMessage && segment.Section != MailContentSection.CurrentMessage)
                continue;

            var text = ProtectedProjectionMarker().Replace(segment.Text ?? string.Empty, string.Empty).Trim();
            if (text.Length == 0)
                continue;

            var paragraph = "<p>" + WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>") + "</p>";
            if (builder.Length + paragraph.Length > maximumHtmlLength)
                break;

            builder.Append(paragraph);
        }

        return builder.ToString();
    }

    /// <summary>Plain text of a rewrite result, with block boundaries kept as line breaks.</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = LineBreakTag().Replace(html, "\n");
        text = BlockEndTag().Replace(text, "\n\n");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = HorizontalWhitespace().Replace(text, " ");
        text = SpaceAroundNewline().Replace(text, "\n");
        text = ExtraBlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"⟦/?[ip]\d+⟧", RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedProjectionMarker();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"<\s*/\s*(p|div|li|h[1-6]|blockquote|tr|table|ul|ol)\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockEndTag();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\f\v ]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@" ?\n ?", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceAroundNewline();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExtraBlankLines();
}
