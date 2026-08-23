using System.Collections.Generic;
using System.Linq;
using MailKit;
using MimeKit;

namespace Wino.Services.Extensions;

public static class MailAttachmentExtensions
{
    public static bool HasMailAttachments(this IMessageSummary messageSummary, MimeMessage mimeMessage = null)
        => mimeMessage != null
            ? mimeMessage.GetMailAttachments().Any()
            : messageSummary?.BodyParts?.Any(IsMailAttachment) == true;

    public static IEnumerable<MimePart> GetMailAttachments(this MimeMessage message)
        => message?.Attachments.OfType<MimePart>().Where(IsMailAttachment) ?? [];

    public static bool IsMailAttachment(this MimePart part)
        => part?.IsAttachment == true && !IsSmimeSecurityPart(part.ContentType, part.FileName);

    private static bool IsMailAttachment(BodyPartBasic part)
        => part?.IsAttachment == true && !IsSmimeSecurityPart(part.ContentType, part.FileName);

    private static bool IsSmimeSecurityPart(ContentType contentType, string fileName)
    {
        var mimeType = contentType?.MimeType;

        return mimeType?.Equals("application/pkcs7-signature", System.StringComparison.OrdinalIgnoreCase) == true
            || mimeType?.Equals("application/x-pkcs7-signature", System.StringComparison.OrdinalIgnoreCase) == true
            || mimeType?.Equals("application/pkcs7-mime", System.StringComparison.OrdinalIgnoreCase) == true
            || mimeType?.Equals("application/x-pkcs7-mime", System.StringComparison.OrdinalIgnoreCase) == true
            || fileName?.Equals("smime.p7s", System.StringComparison.OrdinalIgnoreCase) == true
            || fileName?.Equals("smime.p7m", System.StringComparison.OrdinalIgnoreCase) == true;
    }
}
