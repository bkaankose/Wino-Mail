using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using Google.Apis.Gmail.v1.Data;
using HtmlAgilityPack;
using MimeKit;
using MimeKit.IO;
using MimeKit.IO.Filters;
using MimeKit.Utils;
using Wino.Core.Domain.Extensions;
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

            return MimeMessage.Load(filtered).RecoverHeaderCharsets();
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
            var contentIdsBySource = new Dictionary<string, string>(StringComparer.Ordinal);
            int generatedNameCount = 0;

            foreach (var node in imgNodes)
            {
                var src = node.GetAttributeValue("src", string.Empty);

                if (string.IsNullOrEmpty(src)) continue;

                if (contentIdsBySource.TryGetValue(src, out var existingContentId))
                {
                    node.SetAttributeValue("src", $"cid:{existingContentId}");
                    continue;
                }

                if (!TryParseImageDataUri(HtmlEntity.DeEntitize(src), out var mediaSubtype, out var imageData))
                    continue;

                var extension = GetImageFileExtension(mediaSubtype);
                var fileName = CreateImageFileName(HtmlEntity.DeEntitize(node.GetAttributeValue("alt", string.Empty)), extension);

                if (fileName == null)
                {
                    generatedNameCount++;
                    fileName = $"image{generatedNameCount:000}.{extension}";
                }

                var image = new MimePart("image", mediaSubtype)
                {
                    ContentId = MimeUtils.GenerateMessageId(),
                    Content = new MimeContent(new MemoryStream(imageData)),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = fileName },
                    ContentTransferEncoding = ContentEncoding.Base64
                };

                image.ContentType.Name = fileName;

                bodyBuilder.LinkedResources.Add(image);
                contentIdsBySource[src] = image.ContentId;

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

    internal static string GetImageFileExtension(string? mediaSubtype)
    {
        return mediaSubtype?.ToLowerInvariant() switch
        {
            "jpeg" or "pjpeg" => "jpg",
            "svg+xml" => "svg",
            "x-icon" or "vnd.microsoft.icon" => "ico",
            null or "" => "bin",
            var subtype => subtype
        };
    }

    private const int MaximumImageFileNameLength = 100;

    private static string? CreateImageFileName(string? alt, string extension)
    {
        if (string.IsNullOrWhiteSpace(alt)) return null;

        var builder = new StringBuilder(alt.Length);

        foreach (var character in alt)
        {
            if (char.IsControl(character) || character is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                continue;

            builder.Append(character);
        }

        var baseName = builder.ToString().Trim().TrimEnd('.').Trim();
        var suffix = $".{extension}";

        if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^suffix.Length].TrimEnd('.').Trim();
        else if (extension == "jpg" && baseName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^".jpeg".Length].TrimEnd('.').Trim();

        if (baseName.Length > MaximumImageFileNameLength)
        {
            baseName = baseName[..MaximumImageFileNameLength];

            if (char.IsHighSurrogate(baseName[^1]))
                baseName = baseName[..^1];

            baseName = baseName.TrimEnd('.').Trim();
        }

        return baseName.Length == 0 ? null : baseName + suffix;
    }

    private static bool TryParseImageDataUri(string? uri, [NotNullWhen(true)] out string? mediaSubtype, [NotNullWhen(true)] out byte[]? data)
    {
        mediaSubtype = null;
        data = null;

        if (string.IsNullOrEmpty(uri)) return false;

        uri = uri.Trim();

        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;

        int commaIndex = uri.IndexOf(',');
        if (commaIndex < 0) return false;

        var header = uri[5..commaIndex].Split(';');
        var mediaType = header[0].Trim().ToLowerInvariant();

        if (!mediaType.StartsWith("image/", StringComparison.Ordinal)) return false;

        var subtype = mediaType["image/".Length..];
        if (subtype.Length == 0 || !IsMimeToken(subtype)) return false;

        bool isBase64 = false;

        for (int i = 1; i < header.Length; i++)
        {
            if (header[i].Trim().Equals("base64", StringComparison.OrdinalIgnoreCase))
                isBase64 = true;
        }

        var payload = uri[(commaIndex + 1)..];

        try
        {
            byte[] bytes;

            if (isBase64)
            {
                var base64 = payload.IndexOf('%') < 0 ? payload : Encoding.Latin1.GetString(PercentDecode(payload));

                bytes = Convert.FromBase64String(base64);
            }
            else
            {
                bytes = PercentDecode(payload);
            }

            if (bytes.Length == 0) return false;

            mediaSubtype = subtype;
            data = bytes;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsMimeToken(string value)
    {
        foreach (var character in value)
        {
            if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '!' or '#' or '$' or '&' or '^' or '_' or '.' or '+' or '-')
                continue;

            return false;
        }

        return true;
    }

    private static byte[] PercentDecode(string value)
    {
        if (value.IndexOf('%') < 0) return Encoding.UTF8.GetBytes(value);

        using var output = new MemoryStream(value.Length);
        int literalStart = 0;

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') continue;

            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
                throw new FormatException("Invalid percent-encoding in data URI.");

            WriteUtf8(output, value, literalStart, i - literalStart);
            output.WriteByte(Convert.FromHexString(value.AsSpan(i + 1, 2))[0]);

            i += 2;
            literalStart = i + 1;
        }

        WriteUtf8(output, value, literalStart, value.Length - literalStart);

        return output.ToArray();
    }

    private static void WriteUtf8(Stream output, string value, int start, int length)
    {
        if (length <= 0) return;

        var bytes = Encoding.UTF8.GetBytes(value.Substring(start, length));
        output.Write(bytes, 0, bytes.Length);
    }
}
