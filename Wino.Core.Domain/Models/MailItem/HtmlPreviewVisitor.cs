using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using MimeKit;
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

    private static readonly Regex JsonLdScriptRegex = new(
        """<script\b(?=[^>]*\btype\s*=\s*(?:"application/ld\+json(?:\s*;[^"]*)?"|'application/ld\+json(?:\s*;[^']*)?'|application/ld\+json(?:\s*;[^\s>]*)?))[^>]*>.*?</script\s*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly List<MultipartRelated> stack = [];
    private readonly List<MimeEntity> attachments = [];

    readonly string tempDir;

    public string Body { get; set; }

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

    // S/MIME note: signature verification and decryption run in the background companion
    // (ISmimeService.PrepareSmimeRenderAsync); this visitor receives the already
    // decrypted/extracted message and never executes cryptography. multipart/signed falls
    // through to the default multipart visit; pkcs7 parts are treated as attachments.

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
                if (!TrySanitizeUrlValue(attributeName, attribute.Value, out var sanitizedUrl))
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

    private static bool IsLinkAttribute(string attributeName)
        => string.Equals(attributeName, "href", StringComparison.OrdinalIgnoreCase)
           || string.Equals(attributeName, "xlink:href", StringComparison.OrdinalIgnoreCase);

    private static bool TrySanitizeUrlValue(string attributeName, string rawValue, out string sanitizedValue)
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

        sanitizedValue = IsLinkAttribute(attributeName) ? NormalizeLinkUrl(value) : value;
        return true;
    }

    private static string NormalizeLinkUrl(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal))
            return $"https:{value}";

        if (Uri.TryCreate(value, UriKind.Absolute, out _))
            return value;

        if (IsExplicitRelativeUrl(value))
            return value;

        return LooksLikeHostRelativeUrl(value) ? $"https://{value}" : value;
    }

    private static bool IsExplicitRelativeUrl(string value)
        => value.StartsWith("/", StringComparison.Ordinal)
           || value.StartsWith("./", StringComparison.Ordinal)
           || value.StartsWith("../", StringComparison.Ordinal)
           || value.StartsWith("#", StringComparison.Ordinal)
           || value.StartsWith("?", StringComparison.Ordinal);

    private static bool LooksLikeHostRelativeUrl(string value)
    {
        if (value.Contains('\\'))
            return false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
                return false;
        }

        var hostCandidate = value;
        var separatorIndex = value.IndexOfAny(['/', '?', '#']);

        if (separatorIndex >= 0)
            hostCandidate = value[..separatorIndex];

        if (string.IsNullOrWhiteSpace(hostCandidate) || hostCandidate.Contains('@'))
            return false;

        var portIndex = hostCandidate.LastIndexOf(':');

        if (portIndex > 0)
        {
            var portCandidate = hostCandidate[(portIndex + 1)..];

            if (!int.TryParse(portCandidate, out _))
                return false;

            hostCandidate = hostCandidate[..portIndex];
        }

        if (!hostCandidate.Contains('.', StringComparison.Ordinal))
            return false;

        return Uri.CheckHostName(hostCandidate.TrimEnd('.')) != UriHostNameType.Unknown;
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

        var text = entity.IsHtml ? RemoveJsonLdScripts(entity.Text) : entity.Text;

        Body = converter.Convert(text);
    }

    private static string RemoveJsonLdScripts(string html)
        => string.IsNullOrEmpty(html) ? html : JsonLdScriptRegex.Replace(html, string.Empty);

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
        // realistically, if we've gotten this far, then we can treat this as an attachment
        // even if the IsAttachment property is false.
        attachments.Add(entity);
    }
}
