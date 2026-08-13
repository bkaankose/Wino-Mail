using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Cryptography;
using MimeKit.Text;
using MimeKit.Tnef;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Extracts a display body, inline resources, attachments, and S/MIME signature
/// information from a MIME message.
/// </summary>
public sealed class HtmlPreviewVisitor : MimeVisitor
{
    private static readonly HashSet<string> BlockedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet",
        "base", "meta", "form", "link"
    };

    // SVG is deliberately excluded because an SVG data document may contain active content.
    private static readonly HashSet<string> InlineImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp",
        "image/bmp", "image/x-icon", "image/vnd.microsoft.icon", "image/avif"
    };

    private static readonly HashSet<string> LinkSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp, Uri.UriSchemeHttps, Uri.UriSchemeMailto, "tel"
    };

    private static readonly HashSet<string> ResourceSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp, Uri.UriSchemeHttps, "cid"
    };

    private static readonly Regex ScriptBlockRegex = new(
        @"<script\b[^>]*>.*?(?:</script\s*>|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly Func<SecureMimeContext> secureMimeContextFactory;
    private readonly List<MultipartRelated> relatedStack = [];
    private readonly List<MimeEntity> attachments = [];
    private readonly HashSet<MimeEntity> attachmentSet = new(ReferenceEqualityComparer.Instance);
    private readonly List<Exception> cryptographyErrors = [];

    /// <summary>
    /// Creates a visitor that uses the Windows certificate store for S/MIME operations.
    /// </summary>
    public HtmlPreviewVisitor()
        : this(static () => new WindowsSecureMimeContext())
    {
    }

    /// <summary>
    /// Kept for source compatibility. Inline images are emitted as data URLs, so a
    /// temporary directory is no longer required.
    /// </summary>
    public HtmlPreviewVisitor(string _)
        : this()
    {
    }

    internal HtmlPreviewVisitor(Func<SecureMimeContext> secureMimeContextFactory)
    {
        this.secureMimeContextFactory = secureMimeContextFactory
            ?? throw new ArgumentNullException(nameof(secureMimeContextFactory));
    }

    /// <summary>
    /// Gets the sanitized HTML body selected for display.
    /// </summary>
    public string HtmlBody => Body ?? string.Empty;

    /// <summary>
    /// Gets the selected body before the empty-string fallback is applied.
    /// </summary>
    public string Body { get; private set; }

    /// <summary>
    /// Gets MIME entities that should be presented as attachments.
    /// </summary>
    public IList<MimeEntity> Attachments => attachments;

    /// <summary>
    /// Gets signatures and the result of validating each signature.
    /// </summary>
    public Dictionary<IDigitalSignature, bool> Signatures { get; } = [];

    /// <summary>
    /// Gets recoverable S/MIME errors encountered while preparing the preview.
    /// A detached signed message can still be rendered when verification fails.
    /// </summary>
    public IReadOnlyList<Exception> CryptographyErrors => cryptographyErrors;

    protected override void VisitMultipartAlternative(MultipartAlternative alternative)
    {
        var preferred = alternative
            .Select((entity, index) => new { Entity = entity, Index = index, Score = GetBodyScore(entity) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Index)
            .FirstOrDefault();

        preferred?.Entity.Accept(this);
    }

    protected override void VisitMultipartRelated(MultipartRelated related)
    {
        relatedStack.Add(related);

        try
        {
            related.Root?.Accept(this);

            // Inline resources are not attachments unless the sender explicitly marked them so.
            foreach (var entity in related)
            {
                if (!ReferenceEquals(entity, related.Root) && entity.IsAttachment)
                    AddAttachment(entity);
            }
        }
        finally
        {
            relatedStack.RemoveAt(relatedStack.Count - 1);
        }
    }

    protected override void VisitMultipartSigned(MultipartSigned signed)
    {
        if (IsSecureMimeSignature(signed))
        {
            try
            {
                using var context = secureMimeContextFactory();
                RecordSignatures(signed.Verify(context));
            }
            catch (Exception ex) when (IsRecoverableCryptographyError(ex))
            {
                cryptographyErrors.Add(ex);
            }
        }

        // The first part is clear text. Render it even when signature verification fails.
        if (signed.Count > 0)
            signed[0].Accept(this);
    }

    protected override void VisitTextPart(TextPart entity)
    {
        if (entity.IsAttachment || Body != null)
        {
            AddAttachment(entity);
            return;
        }

        var text = entity.Text ?? string.Empty;

        if (entity.IsHtml)
        {
            Body = new HtmlToHtml
            {
                HtmlTagCallback = SanitizeHtmlTag
            }.Convert(ScriptBlockRegex.Replace(text, string.Empty));

            return;
        }

        if (entity.IsFlowed)
        {
            var converter = new FlowedToHtml();

            if (entity.ContentType.Parameters.TryGetValue("delsp", out string deleteSpace))
                converter.DeleteSpace = deleteSpace.Equals("yes", StringComparison.OrdinalIgnoreCase);

            Body = converter.Convert(text);
            return;
        }

        Body = new TextToHtml().Convert(text);
    }

    protected override void VisitTnefPart(TnefPart entity)
    {
        foreach (var attachment in entity.ExtractAttachments())
            AddAttachment(attachment);
    }

    protected override void VisitMessagePart(MessagePart entity)
        => AddAttachment(entity);

    protected override void VisitMimePart(MimePart entity)
    {
        if (entity is not ApplicationPkcs7Mime secureMime)
        {
            AddAttachment(entity);
            return;
        }

        try
        {
            using var context = secureMimeContextFactory();

            switch (secureMime.SecureMimeType)
            {
                case SecureMimeType.SignedData:
                    var signatures = secureMime.Verify(context, out var signedContent);
                    RecordSignatures(signatures);
                    signedContent?.Accept(this);
                    return;

                case SecureMimeType.EnvelopedData:
                case SecureMimeType.AuthEnvelopedData:
                    secureMime.Decrypt(context)?.Accept(this);
                    return;

                case SecureMimeType.CompressedData:
                    secureMime.Decompress(context)?.Accept(this);
                    return;
            }
        }
        catch (Exception ex) when (IsRecoverableCryptographyError(ex))
        {
            cryptographyErrors.Add(ex);
        }

        // Keep unsupported or unreadable PKCS#7 content out of the normal attachment list.
        // MimeFileService exposes the S/MIME state separately.
    }

    private static int GetBodyScore(MimeEntity entity)
    {
        if (entity.IsAttachment)
            return 0;

        return entity switch
        {
            TextPart text when IsCalendarText(text) => 1,
            TextPart text when text.IsHtml => 4,
            TextPart text when text.IsPlain => 3,
            TextPart => 2,
            MultipartRelated related when related.Root != null => GetBodyScore(related.Root),
            MultipartAlternative alternative => alternative.Count == 0
                ? 0
                : alternative.Max(GetBodyScore),
            MultipartSigned signed when signed.Count > 0 => GetBodyScore(signed[0]),
            _ => 0
        };
    }

    private static bool IsCalendarText(TextPart textPart)
        => textPart.ContentType.MimeType.Equals("text/calendar", StringComparison.OrdinalIgnoreCase);

    private static bool IsSecureMimeSignature(MultipartSigned signed)
    {
        var protocol = signed.ContentType.Parameters["protocol"];

        return protocol?.Equals("application/pkcs7-signature", StringComparison.OrdinalIgnoreCase) == true
            || protocol?.Equals("application/x-pkcs7-signature", StringComparison.OrdinalIgnoreCase) == true;
    }

    private void RecordSignatures(DigitalSignatureCollection signatures)
    {
        foreach (var signature in signatures)
        {
            var isValid = false;

            try
            {
                isValid = signature.Verify();
            }
            catch (Exception ex) when (IsRecoverableCryptographyError(ex))
            {
                cryptographyErrors.Add(ex);
            }

            Signatures[signature] = isValid;
        }
    }

    private static bool IsRecoverableCryptographyError(Exception exception)
        => exception is DigitalSignatureVerifyException
            or CertificateNotFoundException
            or System.Security.Cryptography.CryptographicException
            or Org.BouncyCastle.Cms.CmsException
            or FormatException
            or IOException
            or InvalidOperationException
            or NotSupportedException;

    private void AddAttachment(MimeEntity entity)
    {
        if (attachmentSet.Add(entity))
            attachments.Add(entity);
    }

    private void SanitizeHtmlTag(HtmlTagContext context, HtmlWriter writer)
    {
        var tagName = context.TagName;

        if (BlockedTags.Contains(tagName))
        {
            context.DeleteTag = true;
            context.DeleteEndTag = true;
            return;
        }

        if (context.IsEndTag)
        {
            context.WriteTag(writer, true);
            return;
        }

        context.WriteTag(writer, false);

        foreach (var attribute in context.Attributes)
        {
            var attributeName = attribute.Name;

            if (ShouldDropAttribute(tagName, attributeName))
                continue;

            if (attributeName.Equals("srcset", StringComparison.OrdinalIgnoreCase))
            {
                var sanitizedSrcSet = SanitizeSrcSet(attribute.Value);

                if (!string.IsNullOrEmpty(sanitizedSrcSet))
                {
                    writer.WriteAttributeName(attributeName);
                    writer.WriteAttributeValue(sanitizedSrcSet);
                }

                continue;
            }

            if (IsUrlAttribute(attributeName))
            {
                if (TrySanitizeUrl(attributeName, attribute.Value, out var safeUrl))
                {
                    writer.WriteAttributeName(attributeName);
                    writer.WriteAttributeValue(safeUrl);
                }

                continue;
            }

            writer.WriteAttribute(attribute);
        }

        if (context.TagId == HtmlTagId.Body)
            writer.WriteAttribute("oncontextmenu", "return false;");
    }

    private static bool ShouldDropAttribute(string tagName, string attributeName)
        => attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
            || attributeName.Equals("srcdoc", StringComparison.OrdinalIgnoreCase)
            || attributeName.Equals("ping", StringComparison.OrdinalIgnoreCase)
            || tagName.Equals("body", StringComparison.OrdinalIgnoreCase)
                && attributeName.Equals("oncontextmenu", StringComparison.OrdinalIgnoreCase);

    private static bool IsUrlAttribute(string attributeName)
        => attributeName.Equals("href", StringComparison.OrdinalIgnoreCase)
            || attributeName.Equals("xlink:href", StringComparison.OrdinalIgnoreCase)
            || attributeName.Equals("src", StringComparison.OrdinalIgnoreCase)
            || attributeName.Equals("background", StringComparison.OrdinalIgnoreCase)
            || attributeName.Equals("poster", StringComparison.OrdinalIgnoreCase)
            || attributeName.Equals("action", StringComparison.OrdinalIgnoreCase)
            || attributeName.Equals("formaction", StringComparison.OrdinalIgnoreCase);

    private static bool IsLinkAttribute(string attributeName)
        => attributeName.Equals("href", StringComparison.OrdinalIgnoreCase)
            || attributeName.Equals("xlink:href", StringComparison.OrdinalIgnoreCase);

    private bool TrySanitizeUrl(string attributeName, string rawValue, out string safeValue)
    {
        safeValue = null;

        if (string.IsNullOrWhiteSpace(rawValue))
            return false;

        var value = rawValue.Trim().Trim('"', '\'');

        if (value.Any(char.IsControl))
            return false;

        if (!IsLinkAttribute(attributeName) && TryResolveInlineImage(value, out var inlineImage))
        {
            safeValue = inlineImage;
            return true;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            safeValue = $"https:{value}";
            return true;
        }

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsLinkAttribute(attributeName) && IsSafeImageDataUrl(value))
            {
                safeValue = value;
                return true;
            }

            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            var allowedSchemes = IsLinkAttribute(attributeName) ? LinkSchemes : ResourceSchemes;

            if (!allowedSchemes.Contains(absoluteUri.Scheme))
                return false;

            safeValue = value;
            return true;
        }

        // Reject malformed scheme-like values instead of letting the browser reinterpret them.
        var colon = value.IndexOf(':');
        var firstPathSeparator = value.IndexOfAny(['/', '?', '#']);

        if (colon >= 0 && (firstPathSeparator < 0 || colon < firstPathSeparator))
            return false;

        safeValue = IsLinkAttribute(attributeName) ? NormalizeLinkUrl(value) : value;
        return true;
    }

    private string SanitizeSrcSet(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var result = new List<string>();
        var position = 0;

        while (position < value.Length)
        {
            while (position < value.Length && (char.IsWhiteSpace(value[position]) || value[position] == ','))
                position++;

            if (position >= value.Length)
                break;

            var urlStart = position;
            var isDataUrl = value.AsSpan(position).StartsWith("data:", StringComparison.OrdinalIgnoreCase);

            while (position < value.Length
                && !char.IsWhiteSpace(value[position])
                && (isDataUrl || value[position] != ','))
            {
                position++;
            }

            var url = value[urlStart..position];

            while (position < value.Length && char.IsWhiteSpace(value[position]))
                position++;

            var descriptorStart = position;

            while (position < value.Length && value[position] != ',')
                position++;

            var descriptor = value[descriptorStart..position].Trim();

            if (TrySanitizeUrl("src", url, out var safeUrl))
                result.Add(string.IsNullOrEmpty(descriptor) ? safeUrl : $"{safeUrl} {descriptor}");
        }

        return string.Join(", ", result);
    }

    private bool TryResolveInlineImage(string url, out string dataUrl)
    {
        dataUrl = null;

        if (!TryFindRelatedPart(url, out var image)
            || !InlineImageMimeTypes.Contains(image.ContentType.MimeType)
            || image.Content == null)
        {
            return false;
        }

        using var memory = new MemoryStream();
        image.Content.DecodeTo(memory);
        dataUrl = $"data:{image.ContentType.MimeType};base64,{Convert.ToBase64String(memory.GetBuffer(), 0, (int)memory.Length)}";
        return true;
    }

    private bool TryFindRelatedPart(string url, out MimePart image)
    {
        image = null;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri);
        var contentId = NormalizeContentId(url);

        for (var stackIndex = relatedStack.Count - 1; stackIndex >= 0; stackIndex--)
        {
            var related = relatedStack[stackIndex];

            if (uri != null)
            {
                var index = related.IndexOf(uri);

                if (index >= 0 && related[index] is MimePart uriMatch)
                {
                    image = uriMatch;
                    return true;
                }
            }

            foreach (var entity in related)
            {
                if (entity is MimePart candidate
                    && !string.IsNullOrWhiteSpace(candidate.ContentId)
                    && candidate.ContentId.Trim('<', '>').Equals(contentId, StringComparison.OrdinalIgnoreCase))
                {
                    image = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeContentId(string value)
    {
        var normalized = value.Trim().Trim('\'', '"', '<', '>');

        if (normalized.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];

        return normalized.Trim('<', '>');
    }

    private static bool IsSafeImageDataUrl(string value)
    {
        var comma = value.IndexOf(',');

        if (comma <= "data:".Length)
            return false;

        var metadata = value["data:".Length..comma];
        var mimeType = metadata.Split(';', 2, StringSplitOptions.TrimEntries)[0];

        return InlineImageMimeTypes.Contains(mimeType);
    }

    private static string NormalizeLinkUrl(string value)
    {
        if (IsExplicitRelativeUrl(value))
            return value;

        return LooksLikeHostUrl(value) ? $"https://{value}" : value;
    }

    private static bool IsExplicitRelativeUrl(string value)
        => value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("./", StringComparison.Ordinal)
            || value.StartsWith("../", StringComparison.Ordinal)
            || value.StartsWith("#", StringComparison.Ordinal)
            || value.StartsWith("?", StringComparison.Ordinal);

    private static bool LooksLikeHostUrl(string value)
    {
        if (value.Contains('\\') || value.Any(char.IsWhiteSpace))
            return false;

        var separator = value.IndexOfAny(['/', '?', '#']);
        var host = separator >= 0 ? value[..separator] : value;

        if (string.IsNullOrWhiteSpace(host) || host.Contains('@'))
            return false;

        var portSeparator = host.LastIndexOf(':');

        if (portSeparator > 0)
        {
            if (!int.TryParse(host[(portSeparator + 1)..], out _))
                return false;

            host = host[..portSeparator];
        }

        return host.Contains('.', StringComparison.Ordinal)
            && Uri.CheckHostName(host.TrimEnd('.')) != UriHostNameType.Unknown;
    }
}
