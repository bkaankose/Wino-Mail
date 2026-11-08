using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Services.Extensions;

public static class HtmlAgilityPackExtensions
{
    private const string StyleElementAttribute = "#style";

    private static readonly string[] PlainImageAttributes = ["src", "background", "poster", "data"];
    private static readonly string[] SourceSetAttributes = ["srcset", "imagesrcset"];
    private static readonly string[] SvgReferenceAttributes = ["href", "xlink:href"];

    private static readonly Regex CssUrlRegex = new(@"(?is)url\s*\(\s*(?<u>[^)]*?)\s*\)", RegexOptions.Compiled);
    private static readonly Regex CssImageSetRegex = new(@"(?is)(?:-webkit-)?image-set\s*\((?<body>[^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex CssImportRegex = new(@"(?is)@import\s+(?:url\s*\(\s*(?<u>[^)]*?)\s*\)|(?<q>[""'])(?<u>.*?)\k<q>)[^;]*;?", RegexOptions.Compiled);
    private static readonly Regex CssQuotedRegex = new(@"(?s)(?<q>[""'])(?<u>.*?)\k<q>", RegexOptions.Compiled);

    /// <summary>
    /// Clears passive remote image-loading hooks while preserving already-embedded inline images.
    /// </summary>
    public static void ClearImages(this HtmlDocument document)
        => document.ClearRemoteContent((_, _) => BlockedResourceReason.RemoteContent);

    /// <summary>
    /// Every remote reference the reader would fetch passively: image-like attributes, source sets, SVG image references,
    /// and <c>url(...)</c>, <c>image-set(...)</c> and <c>@import</c> in style attributes and style blocks.
    /// Embedded images (<c>data:image/</c>, <c>cid:</c>, fragments) are not included. For a style block the attribute is <c>#style</c>.
    /// </summary>
    public static List<(HtmlNode node, string attribute, string url)> CollectRemoteImages(this HtmlDocument document)
    {
        var references = new List<(HtmlNode node, string attribute, string url)>();

        if (document?.DocumentNode == null)
        {
            return references;
        }

        var seen = new HashSet<(HtmlNode, string, string)>();

        void Add(HtmlNode node, string attribute, string url)
        {
            var trimmed = Unquote(url);

            if (trimmed.Length == 0 || IsEmbeddedImageSource(trimmed))
            {
                return;
            }

            if (seen.Add((node, attribute, trimmed)))
            {
                references.Add((node, attribute, trimmed));
            }
        }

        foreach (var node in document.DocumentNode.Descendants())
        {
            if (node.NodeType != HtmlNodeType.Element)
            {
                continue;
            }

            foreach (var attribute in PlainImageAttributes)
            {
                var value = node.GetAttributeValue(attribute, null);

                if (!string.IsNullOrWhiteSpace(value))
                {
                    Add(node, attribute, value);
                }
            }

            foreach (var attribute in SourceSetAttributes)
            {
                var value = node.GetAttributeValue(attribute, null);

                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                foreach (var candidate in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var url = candidate.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

                    if (url != null)
                    {
                        Add(node, attribute, url);
                    }
                }
            }

            if (IsSvgImageReferenceElement(node))
            {
                foreach (var attribute in SvgReferenceAttributes)
                {
                    var value = node.GetAttributeValue(attribute, null);

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        Add(node, attribute, value);
                    }
                }
            }

            var style = node.GetAttributeValue("style", null);

            if (!string.IsNullOrWhiteSpace(style))
            {
                foreach (var url in EnumerateCssUrls(style))
                {
                    Add(node, "style", url);
                }
            }

            if (node.Name.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var url in EnumerateCssUrls(node.InnerHtml))
                {
                    Add(node, StyleElementAttribute, url);
                }
            }
        }

        return references;
    }

    /// <summary>
    /// Remote references in the document. Call it before anything is removed.
    /// </summary>
    public static int CountRemoteImages(this HtmlDocument document) => document.CollectRemoteImages().Count;

    /// <summary>
    /// Removes each remote reference <paramref name="shouldBlock"/> returns a reason for, and records it in <paramref name="blocked"/>.
    /// Width and height attributes stay so the layout keeps its size.
    /// </summary>
    public static void ClearRemoteContent(this HtmlDocument document,
                                          Func<HtmlNode, string, BlockedResourceReason?> shouldBlock,
                                          List<BlockedRemoteResource> blocked = null)
    {
        ArgumentNullException.ThrowIfNull(shouldBlock);

        var references = document.CollectRemoteImages();

        if (references.Count == 0)
        {
            return;
        }

        var blockedByTarget = new Dictionary<(HtmlNode node, string attribute), HashSet<string>>();

        foreach (var (node, attribute, url) in references)
        {
            var reason = shouldBlock(node, url);

            if (reason == null)
            {
                continue;
            }

            blocked?.Add(new BlockedRemoteResource(GetHost(url), url, reason.Value));

            if (!blockedByTarget.TryGetValue((node, attribute), out var urls))
            {
                urls = new HashSet<string>(StringComparer.Ordinal);
                blockedByTarget[(node, attribute)] = urls;
            }

            urls.Add(url);
        }

        foreach (var ((node, attribute), urls) in blockedByTarget)
        {
            if (attribute == StyleElementAttribute)
            {
                var css = RemoveBlockedCss(node.InnerHtml, urls);

                if (string.IsNullOrWhiteSpace(css))
                {
                    node.Remove();
                }
                else
                {
                    node.InnerHtml = css;
                }
            }
            else if (attribute == "style")
            {
                var css = RemoveBlockedCss(node.GetAttributeValue("style", string.Empty), urls);

                if (string.IsNullOrWhiteSpace(css))
                {
                    node.Attributes.Remove("style");
                }
                else
                {
                    node.SetAttributeValue("style", css);
                }
            }
            else
            {
                node.Attributes.Remove(attribute);
            }
        }
    }

    /// <summary>
    /// Removes `style` tags from the document.
    /// </summary>
    /// <param name="document"></param>
    public static void ClearStyles(this HtmlDocument document)
    {
        document.DocumentNode
                .Descendants()
                .Where(n => n.Name.Equals("script", StringComparison.OrdinalIgnoreCase)
                || n.Name.Equals("style", StringComparison.OrdinalIgnoreCase)
                || n.Name.Equals("#comment", StringComparison.OrdinalIgnoreCase))
                .ToList()
                .ForEach(n => n.Remove());
    }

    /// <summary>
    /// Returns plain text from the HTML content.
    /// </summary>
    /// <param name="htmlContent">Content to get preview from.</param>
    /// <returns>Text body for the html.</returns>
    public static string GetPreviewText(string htmlContent)
    {
        if (string.IsNullOrEmpty(htmlContent)) return string.Empty;

        HtmlDocument doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        StringWriter sw = new StringWriter();
        ConvertTo(doc.DocumentNode, sw);
        sw.Flush();

        return sw.ToString().Replace(Environment.NewLine, "");
    }

    /// <summary>
    /// Returns readable text for assistive technology while preserving useful block boundaries.
    /// </summary>
    public static string GetAccessibleText(string htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent)) return string.Empty;

        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        using var writer = new StringWriter();
        ConvertToAccessibleText(doc.DocumentNode, writer);

        return NormalizeAccessibleText(writer.ToString());
    }

    private static void ConvertContentTo(HtmlNode node, TextWriter outText)
    {
        foreach (HtmlNode subnode in node.ChildNodes)
        {
            ConvertTo(subnode, outText);
        }
    }

    private static void ConvertTo(HtmlNode node, TextWriter outText)
    {
        string html;
        switch (node.NodeType)
        {
            case HtmlNodeType.Comment:
                // don't output comments
                break;

            case HtmlNodeType.Document:
                ConvertContentTo(node, outText);
                break;

            case HtmlNodeType.Text:
                // script and style must not be output
                string parentName = node.ParentNode.Name;
                if (parentName == "script" || parentName == "style")
                    break;

                // get text
                html = ((HtmlTextNode)node).Text;

                // is it in fact a special closing node output as text?
                if (HtmlNode.IsOverlappedClosingElement(html))
                    break;

                // check the text is meaningful and not a bunch of whitespaces
                if (html.Trim().Length > 0)
                {
                    outText.Write(HtmlEntity.DeEntitize(html));
                }
                break;

            case HtmlNodeType.Element:
                switch (node.Name)
                {
                    case "p":
                        // treat paragraphs as crlf
                        outText.Write("\r\n");
                        break;
                    case "br":
                        outText.Write("\r\n");
                        break;
                }

                if (node.HasChildNodes)
                {
                    ConvertContentTo(node, outText);
                }
                break;
        }
    }

    private static void ConvertToAccessibleText(HtmlNode node, TextWriter outText)
    {
        switch (node.NodeType)
        {
            case HtmlNodeType.Comment:
                return;

            case HtmlNodeType.Document:
                ConvertAccessibleChildren(node, outText);
                return;

            case HtmlNodeType.Text:
                var parentName = node.ParentNode?.Name;

                if (string.Equals(parentName, "script", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(parentName, "style", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var html = ((HtmlTextNode)node).Text;

                if (!HtmlNode.IsOverlappedClosingElement(html) && html.Trim().Length > 0)
                {
                    outText.Write(HtmlEntity.DeEntitize(html));
                    outText.Write(' ');
                }

                return;

            case HtmlNodeType.Element:
                var nodeName = node.Name.ToLowerInvariant();

                if (nodeName is "br")
                {
                    outText.WriteLine();
                    return;
                }

                if (nodeName is "img")
                {
                    var altText = node.GetAttributeValue("alt", string.Empty);

                    if (!string.IsNullOrWhiteSpace(altText))
                    {
                        outText.Write(HtmlEntity.DeEntitize(altText));
                        outText.WriteLine();
                    }

                    return;
                }

                if (nodeName is "li")
                {
                    outText.Write("- ");
                }

                ConvertAccessibleChildren(node, outText);

                if (nodeName is "p" or "div" or "section" or "article" or "header" or "footer" or "li" or "tr"
                    or "table" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                {
                    outText.WriteLine();
                }

                return;
        }
    }

    private static void ConvertAccessibleChildren(HtmlNode node, TextWriter outText)
    {
        foreach (var subnode in node.ChildNodes)
        {
            ConvertToAccessibleText(subnode, outText);
        }
    }

    private static string NormalizeAccessibleText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalizedLineEndings = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalizedLineEndings
            .Split('\n')
            .Select(line => Regex.Replace(line, @"[ \t\f\v]+", " ").Trim())
            .Select(line => Regex.Replace(line, @"\s+([.,;:!?])", "$1"))
            .Where(line => line.Length > 0);

        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsEmbeddedImageSource(string value)
    {
        var trimmed = value.Trim().Trim('"', '\'');

        return trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith('#');
    }

    private static bool IsSvgImageReferenceElement(HtmlNode node)
        => node.Name.Equals("image", StringComparison.OrdinalIgnoreCase)
           || node.Name.Equals("feImage", StringComparison.OrdinalIgnoreCase)
           || node.Name.Equals("use", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateCssUrls(string css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            yield break;
        }

        foreach (Match match in CssImportRegex.Matches(css))
        {
            yield return match.Groups["u"].Value;
        }

        foreach (Match match in CssUrlRegex.Matches(css))
        {
            yield return match.Groups["u"].Value;
        }

        foreach (Match match in CssImageSetRegex.Matches(css))
        {
            foreach (Match quoted in CssQuotedRegex.Matches(match.Groups["body"].Value))
            {
                yield return quoted.Groups["u"].Value;
            }
        }
    }

    private static string RemoveBlockedCss(string css, HashSet<string> blockedUrls)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return string.Empty;
        }

        var result = CssImportRegex.Replace(css, match => blockedUrls.Contains(Unquote(match.Groups["u"].Value)) ? string.Empty : match.Value);
        result = CssUrlRegex.Replace(result, match => blockedUrls.Contains(Unquote(match.Groups["u"].Value)) ? "none" : match.Value);
        result = CssImageSetRegex.Replace(result, match =>
            CssQuotedRegex.Matches(match.Groups["body"].Value).Any(quoted => blockedUrls.Contains(Unquote(quoted.Groups["u"].Value)))
                ? "none"
                : match.Value);

        return result.Trim();
    }

    private static string Unquote(string value) => value.Trim().Trim('"', '\'').Trim();

    private static string GetHost(string url)
    {
        var candidate = url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.IdnHost.ToLowerInvariant()
            : string.Empty;
    }
}
