using System;
using System.Collections.Generic;
using System.IO;
using MimeKit;
using MimeKit.Cryptography;
using MimeKit.Text;
using MimeKit.Tnef;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Visits a MimeMessage and generates HTML suitable to be rendered by a browser control.
/// </summary>
public class HtmlPreviewVisitor : MimeVisitor
{
    private static readonly HashSet<string> BlockedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet", "base", "meta", "form", "link"
    };

    private static readonly HashSet<string> AllowedDataImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp", "image/bmp", "image/x-icon", "image/avif", "image/svg+xml"
    };

    private readonly List<MultipartRelated> stack = [];
    private readonly List<MimeEntity> attachments = [];

    readonly string tempDir;

    public string Body { get; set; }
    public Dictionary<IDigitalSignature, bool> Signatures = [];

    /// <summary>
    /// Creates a new HtmlPreviewVisitor.
    /// </summary>
    /// <param name="tempDirectory">A temporary directory used for storing image files.</param>
    public HtmlPreviewVisitor(string tempDirectory)
    {
        tempDir = tempDirectory;
    }

    /// <summary>
    /// The list of attachments that were in the MimeMessage.
    /// </summary>
    public IList<MimeEntity> Attachments
    {
        get { return attachments; }
    }

    /// <summary>
    /// The HTML string that can be set on the BrowserControl.
    /// </summary>
    public string HtmlBody
    {
        get { return Body ?? string.Empty; }
    }

    protected override void VisitMultipartAlternative(MultipartAlternative alternative)
    {
        // Prefer rich body alternatives first, and only fall back to calendar text if nothing else exists.
        for (int i = alternative.Count - 1; i >= 0 && Body == null; i--)
        {
            if (IsCalendarText(alternative[i]))
                continue;

            alternative[i].Accept(this);
        }

        for (int i = alternative.Count - 1; i >= 0 && Body == null; i--)
        {
            if (!IsCalendarText(alternative[i]))
                continue;

            alternative[i].Accept(this);
        }
    }

    protected override void VisitMultipartRelated(MultipartRelated related)
    {
        var root = related.Root;

        // push this multipart/related onto our stack
        stack.Add(related);

        // visit the root document
        root.Accept(this);

        // pop this multipart/related off our stack
        stack.RemoveAt(stack.Count - 1);
    }

    protected override void VisitMultipartSigned(MultipartSigned signed)
    {
        VerifySignatures(signed.Verify());
        VisitMultipart(signed);
    }

    // look up the image based on the img src url within our multipart/related stack
    bool TryGetImage(string url, out MimePart image)
    {
        image = null;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        UriKind kind;
        int index;
        Uri uri = null;

        if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
            kind = UriKind.Absolute;
        else if (Uri.IsWellFormedUriString(url, UriKind.Relative))
            kind = UriKind.Relative;
        else
            kind = UriKind.RelativeOrAbsolute;

        try
        {
            uri = new Uri(url, kind);
        }
        catch
        {
            // noop: we still attempt CID/content-id lookup below.
        }

        for (int i = stack.Count - 1; i >= 0; i--)
        {
            if (uri != null && (index = stack[i].IndexOf(uri)) != -1)
            {
                image = stack[i][index] as MimePart;

                if (image != null)
                    return true;
            }

            var normalizedContentId = NormalizeContentId(url);

            if (string.IsNullOrEmpty(normalizedContentId))
                continue;

            foreach (var relatedPart in stack[i])
            {
                if (relatedPart is not MimePart candidate || string.IsNullOrEmpty(candidate.ContentId))
                    continue;

                if (string.Equals(candidate.ContentId.Trim('<', '>'), normalizedContentId, StringComparison.OrdinalIgnoreCase))
                {
                    image = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeContentId(string url)
    {
        var trimmed = url.Trim().Trim('\'', '"', '<', '>');

        if (trimmed.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[4..];

        return trimmed.Trim('<', '>');
    }

    // Save the image to our temp directory and return a "file://" url suitable for
    // the browser control to load.
    // Note: if you'd rather embed the image data into the HTML, you can construct a
    // "data:" url instead.
    string SaveImage(MimePart image)
    {
        using (var memory = new MemoryStream())
        {
            image.Content.DecodeTo(memory);
            var buffer = memory.GetBuffer();
            var length = (int)memory.Length;
            var base64 = Convert.ToBase64String(buffer, 0, length);

            return string.Format("data:{0};base64,{1}", image.ContentType.MimeType, base64);
        }
    }

    // Replaces image references that refer to images embedded within the message with
    // "data:" urls the browser control can load. Also sanitizes dangerous tags/attributes.
    void HtmlTagCallback(HtmlTagContext ctx, HtmlWriter htmlWriter)
    {
        var tagName = ctx.TagName;

        if (BlockedTags.Contains(tagName))
        {
            ctx.DeleteTag = true;
            ctx.DeleteEndTag = true;
            return;
        }

        if (ctx.IsEndTag)
        {
            ctx.WriteTag(htmlWriter, true);
            return;
        }

        ctx.WriteTag(htmlWriter, false);

        foreach (var attribute in ctx.Attributes)
        {
            var attributeName = attribute.Name;

            if (ShouldDropAttribute(tagName, attributeName))
                continue;

            if (TryResolveImageAttribute(tagName, attributeName, attribute.Value, out var resolvedValue))
            {
                htmlWriter.WriteAttributeName(attributeName);
                htmlWriter.WriteAttributeValue(resolvedValue);
                continue;
            }

            if (IsUrlAttribute(attributeName))
            {
                if (!TrySanitizeUrlValue(attribute.Value, out var sanitizedUrl))
                    continue;

                htmlWriter.WriteAttributeName(attributeName);
                htmlWriter.WriteAttributeValue(sanitizedUrl);
                continue;
            }

            htmlWriter.WriteAttribute(attribute);
        }

        if (ctx.TagId == HtmlTagId.Body)
            htmlWriter.WriteAttribute("oncontextmenu", "return false;");
    }

    private bool TryResolveImageAttribute(string tagName, string attributeName, string value, out string resolvedValue)
    {
        resolvedValue = null;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var lowerAttributeName = attributeName.ToLowerInvariant();
        var isImageTag = string.Equals(tagName, "img", StringComparison.OrdinalIgnoreCase);

        if (isImageTag && lowerAttributeName == "srcset")
        {
            resolvedValue = ResolveSrcSet(value);
            return resolvedValue != value;
        }

        if (lowerAttributeName != "src" && lowerAttributeName != "background" && lowerAttributeName != "poster")
            return false;

        if (TryGetImage(value, out var image))
        {
            resolvedValue = SaveImage(image);
            return true;
        }

        return false;
    }

    private string ResolveSrcSet(string srcSetValue)
    {
        var candidates = srcSetValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var updatedCandidates = new List<string>(candidates.Length);

        foreach (var candidate in candidates)
        {
            var parts = candidate.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
                continue;

            var imageSource = parts[0];

            if (TryGetImage(imageSource, out var image))
                imageSource = SaveImage(image);

            updatedCandidates.Add(parts.Length == 2 ? $"{imageSource} {parts[1]}" : imageSource);
        }

        return string.Join(", ", updatedCandidates);
    }

    private static bool ShouldDropAttribute(string tagName, string attributeName)
    {
        if (attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(tagName, "body", StringComparison.OrdinalIgnoreCase)
            && string.Equals(attributeName, "oncontextmenu", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(attributeName, "srcdoc", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsUrlAttribute(string attributeName)
        => string.Equals(attributeName, "href", StringComparison.OrdinalIgnoreCase)
           || string.Equals(attributeName, "src", StringComparison.OrdinalIgnoreCase)
           || string.Equals(attributeName, "action", StringComparison.OrdinalIgnoreCase)
           || string.Equals(attributeName, "xlink:href", StringComparison.OrdinalIgnoreCase)
           || string.Equals(attributeName, "background", StringComparison.OrdinalIgnoreCase)
           || string.Equals(attributeName, "poster", StringComparison.OrdinalIgnoreCase);

    private static bool TrySanitizeUrlValue(string rawValue, out string sanitizedValue)
    {
        sanitizedValue = null;

        if (string.IsNullOrWhiteSpace(rawValue))
            return false;

        var value = rawValue.Trim().Trim('"', '\'');

        if (value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !IsAllowedImageDataUrl(value))
            return false;

        sanitizedValue = value;
        return true;
    }

    private static bool IsAllowedImageDataUrl(string value)
    {
        const string dataPrefix = "data:";

        if (!value.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var payloadStart = value.IndexOf(',', StringComparison.Ordinal);

        if (payloadStart <= dataPrefix.Length)
            return false;

        var metadata = value[dataPrefix.Length..payloadStart];
        var metadataParts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (metadataParts.Length == 0)
            return false;

        return AllowedDataImageMimeTypes.Contains(metadataParts[0]);
    }

    protected override void VisitTextPart(TextPart entity)
    {
        TextConverter converter;

        if (Body != null)
        {
            // since we've already found the body, treat this as an attachment
            attachments.Add(entity);
            return;
        }

        if (entity.IsHtml)
        {
            converter = new HtmlToHtml
            {
                HtmlTagCallback = HtmlTagCallback
            };
        }
        else if (entity.IsFlowed)
        {
            var flowed = new FlowedToHtml();
            string delsp;

            if (entity.ContentType.Parameters.TryGetValue("delsp", out delsp))
                flowed.DeleteSpace = delsp.ToLowerInvariant() == "yes";

            converter = flowed;
        }
        else
        {
            converter = new TextToHtml();
        }

        Body = converter.Convert(entity.Text);
    }

    private static bool IsCalendarText(MimeEntity entity)
        => entity is TextPart textPart &&
           textPart.ContentType?.MimeType?.Equals("text/calendar", StringComparison.OrdinalIgnoreCase) == true;

    protected override void VisitTnefPart(TnefPart entity)
    {
        // extract any attachments in the MS-TNEF part
        attachments.AddRange(entity.ExtractAttachments());
    }

    protected override void VisitMessagePart(MessagePart entity)
    {
        // treat message/rfc822 parts as attachments
        attachments.Add(entity);
    }

    protected override void VisitMimePart(MimePart entity)
    {
        if (entity is ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.EnvelopedData } encrypted)
        {
            encrypted.Decrypt().Accept(this);
        }
        else if (entity is ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.SignedData } signed)
        {
            MimeEntity extracted;

            VerifySignatures(signed.Verify(out extracted));

            extracted.Accept(this);
        }
        else
        {
            // realistically, if we've gotten this far, then we can treat this as an attachment
            // even if the IsAttachment property is false.
            attachments.Add(entity);
        }
    }

    private void VerifySignatures(DigitalSignatureCollection signatures)
    {
        foreach (var signature in signatures)
        {
            try
            {
                bool valid = signature.Verify();
                Signatures.Add(signature, valid);
            }
            catch (DigitalSignatureVerifyException)
            {
                // There was an error verifying the signature.
                Signatures.Add(signature, false);
            }
        }
    }
}
