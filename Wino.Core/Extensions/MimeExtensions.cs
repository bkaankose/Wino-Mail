using System;
using System.IO;
using System.Text;
using Google.Apis.Gmail.v1.Data;
using HtmlAgilityPack;
using MimeKit;
using MimeKit.IO;
using MimeKit.IO.Filters;
using MimeKit.Utils;

namespace Wino.Core.Extensions
{
    public static class MimeExtensions
    {
        /// <summary>
        /// Returns MimeKit.MimeMessage instance for this GMail Message's Raw content.
        /// </summary>
        /// <param name="message">GMail message.</param>
        public static MimeMessage GetGmailMimeMessage(this Message message)
        {
            if (message == null || message.Raw == null)
                return null;

            // Gmail raw is not base64 but base64Safe. We need to remove this HTML things.
            var base64Encoded = message.Raw.Replace(",", "=").Replace("-", "+").Replace("_", "/");

            byte[] bytes = Encoding.ASCII.GetBytes(base64Encoded);

            var stream = new MemoryStream(bytes);

            // This method will dispose outer stream.

            using (stream)
            {
                using var filtered = new FilteredStream(stream);
                filtered.Add(DecoderFilter.Create(ContentEncoding.Base64));

                return MimeMessage.Load(filtered);
            }
        }


        /// <summary>
        /// Sets html body replacing base64 images with cid linked resources.
        /// Updates text body based on html.
        /// </summary>
        /// <param name="bodyBuilder">Body builder.</param>
        /// <param name="htmlContent">Html content that can have embedded images.</param>
        /// <returns>Body builder with set HtmlBody.</returns>
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
}
