using System;
using System.IO;
using HtmlAgilityPack;
using MimeKit;
using MimeKit.Utils;

namespace Wino.Services.Extensions;

public static class MimeBodyExtensions
{
    /// <summary>
    /// Sets the HTML body of the builder, converting embedded data:image sources to
    /// linked MIME resources referenced by cid.
    /// </summary>
    public static BodyBuilder SetHtmlBody(this BodyBuilder bodyBuilder, string htmlContent)
    {
        if (string.IsNullOrEmpty(htmlContent)) return bodyBuilder;

        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        var imgNodes = doc.DocumentNode.SelectNodes("//img");

        if (imgNodes != null)
        {
            foreach (var node in imgNodes)
            {
                var src = node.GetAttributeValue("src", string.Empty);

                if (string.IsNullOrEmpty(src)) continue;

                if (!src.StartsWith("data:image"))
                {
                    continue;
                }

                var parts = src.Substring(11).Split([";base64,"], StringSplitOptions.None);

                string mimeType = parts[0];
                string base64Content = parts[1];

                var alt = node.GetAttributeValue("alt", $"Embedded_Image.{mimeType}");

                // Convert the base64 content to binary data
                byte[] imageData = Convert.FromBase64String(base64Content);

                // Create a new linked resource as MimePart
                var image = new MimePart("image", mimeType)
                {
                    ContentId = MimeUtils.GenerateMessageId(),
                    Content = new MimeContent(new MemoryStream(imageData)),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
                    ContentDescription = alt.Replace(" ", "_"),
                    FileName = alt,
                    ContentTransferEncoding = ContentEncoding.Base64
                };

                bodyBuilder.LinkedResources.Add(image);

                node.SetAttributeValue("src", $"cid:{image.ContentId}");
            }
        }

        bodyBuilder.HtmlBody = doc.DocumentNode.InnerHtml;

        if (!string.IsNullOrEmpty(bodyBuilder.HtmlBody))
        {
            bodyBuilder.TextBody = HtmlAgilityPackExtensions.GetPreviewText(bodyBuilder.HtmlBody);
        }

        return bodyBuilder;
    }
}
