using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Wino.Services.Extensions;

public static class HtmlAgilityPackExtensions
{
    /// <summary>
    /// Clears passive remote image-loading hooks while preserving already-embedded inline images.
    /// </summary>
    public static void ClearImages(this HtmlDocument document)
    {
        if (document?.DocumentNode == null)
        {
            return;
        }

        foreach (var eachNode in document.DocumentNode.Descendants().ToList())
        {
            ClearRemoteImageAttribute(eachNode, "src");
            ClearRemoteImageAttribute(eachNode, "background");
            ClearRemoteImageAttribute(eachNode, "poster");
            ClearRemoteImageAttribute(eachNode, "data");

            if (eachNode.Attributes.Contains("srcset"))
            {
                eachNode.Attributes.Remove("srcset");
            }

            if (eachNode.Attributes.Contains("imagesrcset"))
            {
                eachNode.Attributes.Remove("imagesrcset");
            }

            if (eachNode.Attributes.Contains("style"))
            {
                var sanitizedStyle = SanitizeCss(eachNode.GetAttributeValue("style", string.Empty));

                if (string.IsNullOrWhiteSpace(sanitizedStyle))
                {
                    eachNode.Attributes.Remove("style");
                }
                else
                {
                    eachNode.SetAttributeValue("style", sanitizedStyle);
                }
            }

            if (IsSvgImageReferenceElement(eachNode))
            {
                ClearRemoteImageAttribute(eachNode, "href");
                ClearRemoteImageAttribute(eachNode, "xlink:href");
            }
        }

        foreach (var styleNode in document.DocumentNode.Descendants("style").ToList())
        {
            var sanitizedCss = SanitizeCss(styleNode.InnerHtml);

            if (string.IsNullOrWhiteSpace(sanitizedCss))
            {
                styleNode.Remove();
            }
            else
            {
                styleNode.InnerHtml = sanitizedCss;
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

    private static void ClearRemoteImageAttribute(HtmlNode node, string attributeName)
    {
        var value = node.GetAttributeValue(attributeName, null);

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!IsEmbeddedImageSource(value))
        {
            node.Attributes.Remove(attributeName);
        }
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

    private static string SanitizeCss(string css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return string.Empty;
        }

        var sanitizedCss = Regex.Replace(css, @"(?is)url\s*\([^)]*\)", "none");
        sanitizedCss = Regex.Replace(sanitizedCss, @"(?is)image-set\s*\([^)]*\)", "none");
        sanitizedCss = Regex.Replace(sanitizedCss, @"(?is)@import\s+[^;]+;?", string.Empty);

        return sanitizedCss.Trim();
    }
}
