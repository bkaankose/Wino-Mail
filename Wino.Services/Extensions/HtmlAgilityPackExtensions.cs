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
