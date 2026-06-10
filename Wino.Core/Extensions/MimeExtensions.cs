using System;
using System.IO;
using System.Text;
using Google.Apis.Gmail.v1.Data;
using HtmlAgilityPack;
using MimeKit;
using MimeKit.IO;
using MimeKit.IO.Filters;
using MimeKit.Utils;
using Wino.Services.Extensions;

namespace Wino.Core.Extensions;

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
}
