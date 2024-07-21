using System;
using System.IO;
using System.Linq;
using HtmlAgilityPack;

namespace Wino.Services.Extensions
{
    public static class HtmlAgilityPackExtensions
    {
        /// <summary>
        /// Clears out the src attribute for all `img` and `v:fill` tags.
        /// </summary>
        /// <param name="document"></param>
        public static void ClearImages(this HtmlDocument document)
        {
            if (document.DocumentNode.InnerHtml.Contains("<img"))
            {
                foreach (var eachNode in document.DocumentNode.SelectNodes("//img"))
                {
                    eachNode.Attributes.Remove("src");
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
    }
}
