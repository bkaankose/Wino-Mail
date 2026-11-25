using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Wino.Renderer.Primitives;

namespace Wino.Mail.Rendering;

/// <summary>
/// Moves inline images between the two forms the composer's HTML takes: <c>cid:</c> references with the bytes beside
/// them (a message) and <c>data:</c> URIs (one self-contained string for a signature, a template or calendar notes).
/// </summary>
internal static class EngineEditorHtml
{
    private const string CidScheme = "cid:";
    private const string DataScheme = "data:";

    /// <summary>The content of the body element of the document the engine serializes.</summary>
    public static string BodyOf(string documentHtml)
    {
        var bodyStart = documentHtml.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyStart < 0) return documentHtml;

        var contentStart = documentHtml.IndexOf('>', bodyStart);
        if (contentStart < 0) return documentHtml;

        contentStart++;
        var contentEnd = documentHtml.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

        return contentEnd < contentStart ? documentHtml[contentStart..] : documentHtml[contentStart..contentEnd];
    }

    /// <summary>The body HTML with every <c>cid:</c> image the content carries written as a <c>data:</c> URI.</summary>
    public static string ToSelfContainedBody(OutgoingContent content)
    {
        var body = BodyOf(content.Html);
        if (content.InlineResources.Count == 0) return body;

        return RewriteImageSources(body, source =>
        {
            if (!source.StartsWith(CidScheme, StringComparison.OrdinalIgnoreCase)) return null;

            var contentId = NormalizeContentId(source[CidScheme.Length..]);
            var resource = content.InlineResources.FirstOrDefault(
                item => string.Equals(NormalizeContentId(item.ContentId), contentId, StringComparison.OrdinalIgnoreCase));
            if (resource is null) return null;

            var mediaType = string.IsNullOrWhiteSpace(resource.MediaType) ? "image/png" : resource.MediaType;
            return $"{DataScheme}{mediaType};base64,{Convert.ToBase64String(resource.Bytes.Span)}";
        });
    }

    /// <summary>
    /// The content with every base64 <c>data:</c> image moved into an inline resource. Signatures, templates and
    /// rewritten bodies bring such images, and mail clients do not show <c>data:</c> images in a received message.
    /// </summary>
    public static OutgoingContent ToLinkedResources(OutgoingContent content)
    {
        if (!content.Html.Contains("data:image", StringComparison.OrdinalIgnoreCase)) return content;

        var resources = new List<MailResource>(content.InlineResources);
        var html = RewriteImageSources(content.Html, source =>
        {
            if (!TryReadDataImage(source, out var mediaType, out var bytes)) return null;

            var contentId = $"{Convert.ToHexStringLower(SHA256.HashData(bytes).AsSpan(0, 12))}@wino";
            if (!resources.Any(item => string.Equals(item.ContentId, contentId, StringComparison.OrdinalIgnoreCase)))
                resources.Add(new MailResource { Bytes = bytes, MediaType = mediaType, ContentId = contentId });

            return CidScheme + contentId;
        });

        return new OutgoingContent { Html = html, PlainText = content.PlainText, InlineResources = resources };
    }

    /// <summary>Reads a base64 <c>data:</c> image URI; false for anything else, including SVG text URIs.</summary>
    public static bool TryReadDataImage(string? source, out string mediaType, out byte[] bytes)
    {
        mediaType = string.Empty;
        bytes = [];
        if (source is null || !source.StartsWith(DataScheme, StringComparison.OrdinalIgnoreCase)) return false;

        var comma = source.IndexOf(',');
        if (comma < 0) return false;

        var header = source[DataScheme.Length..comma];
        if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase)) return false;

        var separator = header.IndexOf(';');
        mediaType = (separator < 0 ? header : header[..separator]).Trim().ToLowerInvariant();
        if (!mediaType.StartsWith("image/", StringComparison.Ordinal)) return false;

        try
        {
            bytes = Convert.FromBase64String(source[(comma + 1)..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return bytes.Length > 0;
    }

    private static string NormalizeContentId(string contentId) => contentId.Trim().TrimStart('<').TrimEnd('>');

    /// <summary>
    /// Replaces the <c>src</c> value of every <c>img</c> tag for which <paramref name="replace"/> returns a value.
    /// The value is handed over decoded and the replacement is written as it is, so it must need no escaping.
    /// </summary>
    private static string RewriteImageSources(string html, Func<string, string?> replace)
    {
        var sources = FindImageSources(html);
        if (sources.Count == 0) return html;

        StringBuilder? builder = null;
        var copied = 0;

        foreach (var (start, length) in sources)
        {
            var replacement = replace(WebUtility.HtmlDecode(html.Substring(start, length)).Trim());
            if (replacement is null) continue;

            builder ??= new StringBuilder(html.Length);
            builder.Append(html, copied, start - copied).Append(replacement);
            copied = start + length;
        }

        return builder is null ? html : builder.Append(html, copied, html.Length - copied).ToString();
    }

    private static List<(int Start, int Length)> FindImageSources(string html)
    {
        var sources = new List<(int Start, int Length)>();
        var index = 0;

        while ((index = html.IndexOf("<img", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var position = index + 4;
            if (position >= html.Length) break;

            // "<imgx" is another element.
            if (!char.IsWhiteSpace(html[position]) && html[position] is not ('/' or '>'))
            {
                index = position;
                continue;
            }

            while (position < html.Length && html[position] != '>')
            {
                if (char.IsWhiteSpace(html[position]) || html[position] == '/')
                {
                    position++;
                    continue;
                }

                var nameStart = position;
                while (position < html.Length && !char.IsWhiteSpace(html[position]) && html[position] is not ('=' or '>' or '/'))
                    position++;
                var nameLength = position - nameStart;

                while (position < html.Length && char.IsWhiteSpace(html[position])) position++;
                if (position >= html.Length || html[position] != '=') continue;

                position++;
                while (position < html.Length && char.IsWhiteSpace(html[position])) position++;
                if (position >= html.Length) break;

                int valueStart;
                int valueEnd;
                if (html[position] is '"' or '\'')
                {
                    valueStart = position + 1;
                    valueEnd = html.IndexOf(html[position], valueStart);
                    if (valueEnd < 0) return sources;

                    position = valueEnd + 1;
                }
                else
                {
                    valueStart = position;
                    while (position < html.Length && !char.IsWhiteSpace(html[position]) && html[position] != '>') position++;
                    valueEnd = position;
                }

                if (nameLength == 3 && string.Compare(html, nameStart, "src", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
                    sources.Add((valueStart, valueEnd - valueStart));
            }

            index = position;
        }

        return sources;
    }
}
